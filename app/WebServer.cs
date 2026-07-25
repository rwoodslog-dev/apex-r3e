// Mini serveur HTTP + WebSocket, sans aucune dependance externe.
// Utilise TcpListener plutot que HttpListener : pas besoin de droits admin
// ni de reservation d'URL (netsh), donc double-clic et ca marche.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

class WebServer
{
    const string WS_MAGIC = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    readonly TcpListener listener;
    readonly string indexHtml;
    readonly List<TcpClient> wsClients = new List<TcpClient>();
    readonly object clientLock = new object();
    volatile bool running = true;

    public int Port { get; private set; }

    public WebServer(string html, int preferredPort)
    {
        indexHtml = html;
        // on tente le port demande, sinon on laisse l'OS en choisir un libre
        try
        {
            listener = new TcpListener(IPAddress.Loopback, preferredPort);
            listener.Start();
        }
        catch (SocketException)
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
        }
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Start()
    {
        var t = new Thread(AcceptLoop);
        t.IsBackground = true;
        t.Start();
    }

    void AcceptLoop()
    {
        while (running)
        {
            TcpClient c = null;
            try { c = listener.AcceptTcpClient(); }
            catch { if (!running) return; continue; }

            var th = new Thread(() => Handle(c));
            th.IsBackground = true;
            th.Start();
        }
    }

    void Handle(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            string req = ReadHeaders(stream);
            if (req == null) { client.Close(); return; }

            string wsKey = HeaderValue(req, "Sec-WebSocket-Key");
            bool wantsWs = wsKey != null &&
                           req.IndexOf("upgrade", StringComparison.OrdinalIgnoreCase) >= 0;

            if (wantsWs)
            {
                string accept = Convert.ToBase64String(
                    SHA1.Create().ComputeHash(Encoding.ASCII.GetBytes(wsKey + WS_MAGIC)));
                string resp = "HTTP/1.1 101 Switching Protocols\r\n"
                            + "Upgrade: websocket\r\n"
                            + "Connection: Upgrade\r\n"
                            + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
                byte[] rb = Encoding.ASCII.GetBytes(resp);
                stream.Write(rb, 0, rb.Length);
                stream.Flush();

                lock (clientLock) wsClients.Add(client);
                // on garde la connexion ouverte ; on lit pour detecter la fermeture
                DrainUntilClose(client, stream);
                lock (clientLock) wsClients.Remove(client);
                try { client.Close(); } catch { }
                return;
            }

            // requete HTTP normale
            string path = RequestPath(req);
            byte[] body;
            string ctype;
            if (path == "/" || path == "/index.html")
            {
                body = Encoding.UTF8.GetBytes(indexHtml);
                ctype = "text/html; charset=utf-8";
            }
            else
            {
                body = Encoding.UTF8.GetBytes("404");
                ctype = "text/plain";
            }
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: " + ctype +
                "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
            client.Close();
        }
        catch { try { client.Close(); } catch { } }
    }

    static string ReadHeaders(NetworkStream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        int guard = 0;
        while (guard++ < 16384)
        {
            int n;
            try { n = s.Read(one, 0, 1); } catch { return null; }
            if (n <= 0) return null;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[sb.Length-1] == '\n' && sb[sb.Length-2] == '\r'
                && sb[sb.Length-3] == '\n' && sb[sb.Length-4] == '\r')
                return sb.ToString();
        }
        return null;
    }

    static string HeaderValue(string req, string name)
    {
        foreach (var line in req.Split('\n'))
        {
            int c = line.IndexOf(':');
            if (c < 0) continue;
            if (line.Substring(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return line.Substring(c + 1).Trim();
        }
        return null;
    }

    static string RequestPath(string req)
    {
        int e = req.IndexOf('\n');
        if (e < 0) return "/";
        var parts = req.Substring(0, e).Trim().Split(' ');
        return parts.Length >= 2 ? parts[1] : "/";
    }

    /// <summary>Recoit les messages texte envoyes par le navigateur.</summary>
    public event Action<string> MessageReceived;

    void DrainUntilClose(TcpClient c, NetworkStream s)
    {
        var acc = new List<byte>();
        var buf = new byte[4096];

        while (running && c.Connected)
        {
            int n;
            try { n = s.Read(buf, 0, buf.Length); }
            catch { return; }
            if (n <= 0) return;

            for (int i = 0; i < n; i++) acc.Add(buf[i]);

            // decode toutes les trames completes presentes dans le tampon
            while (true)
            {
                if (acc.Count < 2) break;

                int opcode = acc[0] & 0x0F;
                bool masked = (acc[1] & 0x80) != 0;
                long len = acc[1] & 0x7F;
                int off = 2;

                if (len == 126)
                {
                    if (acc.Count < 4) break;
                    len = (acc[2] << 8) | acc[3];
                    off = 4;
                }
                else if (len == 127)
                {
                    if (acc.Count < 10) break;
                    len = 0;
                    for (int k = 0; k < 8; k++) len = (len << 8) | acc[2 + k];
                    off = 10;
                }

                // garde-fou : un message anormalement gros = flux corrompu
                if (len < 0 || len > 1 << 20) return;

                int maskOff = off;
                if (masked) off += 4;
                if (acc.Count < off + len) break;   // trame incomplete

                if (opcode == 0x8) return;          // fermeture

                if (opcode == 0x1)                  // texte
                {
                    var payload = new byte[len];
                    for (long k = 0; k < len; k++)
                    {
                        byte b = acc[(int)(off + k)];
                        // les trames client->serveur sont TOUJOURS masquees (RFC 6455)
                        if (masked) b = (byte)(b ^ acc[maskOff + (int)(k % 4)]);
                        payload[k] = b;
                    }
                    var h = MessageReceived;
                    if (h != null)
                    {
                        try { h(Encoding.UTF8.GetString(payload)); } catch { }
                    }
                }

                acc.RemoveRange(0, (int)(off + len));
            }
        }
    }

    /// <summary>Envoie un texte a tous les clients connectes.</summary>
    public void Broadcast(string text)
    {
        byte[] frame = EncodeTextFrame(text);
        List<TcpClient> snapshot;
        lock (clientLock) snapshot = new List<TcpClient>(wsClients);

        foreach (var c in snapshot)
        {
            try
            {
                if (!c.Connected) throw new IOException();
                var st = c.GetStream();
                st.Write(frame, 0, frame.Length);
                st.Flush();
            }
            catch
            {
                lock (clientLock) wsClients.Remove(c);
                try { c.Close(); } catch { }
            }
        }
    }

    public int ClientCount { get { lock (clientLock) return wsClients.Count; } }

    /// <summary>Encode une trame WebSocket texte non masquee (serveur -> client).</summary>
    public static byte[] EncodeTextFrame(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        int len = payload.Length;
        byte[] frame;
        int offset;

        if (len <= 125)
        {
            frame = new byte[2 + len];
            frame[1] = (byte)len;
            offset = 2;
        }
        else if (len <= 65535)
        {
            frame = new byte[4 + len];
            frame[1] = 126;
            frame[2] = (byte)((len >> 8) & 0xFF);
            frame[3] = (byte)(len & 0xFF);
            offset = 4;
        }
        else
        {
            frame = new byte[10 + len];
            frame[1] = 127;
            for (int i = 0; i < 8; i++)
                frame[2 + i] = (byte)(((long)len >> ((7 - i) * 8)) & 0xFF);
            offset = 10;
        }
        frame[0] = 0x81; // FIN + opcode texte
        Buffer.BlockCopy(payload, 0, frame, offset, len);
        return frame;
    }

    public void Stop()
    {
        running = false;
        try { listener.Stop(); } catch { }
        lock (clientLock)
        {
            foreach (var c in wsClients) { try { c.Close(); } catch { } }
            wsClients.Clear();
        }
    }
}
