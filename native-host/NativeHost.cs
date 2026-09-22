// Foxy Gestures yt-dlp native messaging host.
//
// Speaks the Firefox native messaging protocol on stdio (4-byte little-endian length
// prefix + UTF-8 JSON). For each request {url, mediaUrl, referer} it spawns yt-dlp with
// the mediaUrl when present (an HLS/DASH manifest captured from the page) else the page
// URL, replies {"ok":true,"pid":N} immediately, and keeps draining yt-dlp's output into
// foxygestures_ytdlp.log until the child exits.
//
// Extra yt-dlp arguments come from foxygestures_ytdlp.args next to this exe: one argument
// per line, '#' starts a comment. That is where the download directory and cookie options
// live, so they can be changed without recompiling.
//
// Build: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe
//        /out:foxygestures_ytdlp.exe NativeHost.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

public class FoxyGesturesYtDlpHost
{
    private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string LogPath = Path.Combine(BaseDir, "foxygestures_ytdlp.log");
    private static Process lastChild = null;

    private static void Log(string message)
    {
        try {
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
        } catch { }
    }

    private static int ReadFully(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count) {
            int n = s.Read(buf, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    // Extract a string field from a JSON message without pulling in a JSON library.
    private static string JsonString(string json, string key)
    {
        Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
        if (!m.Success) return null;
        return m.Groups[1].Value
            .Replace("\\/", "/")
            .Replace("\\\"", "\"")
            .Replace("\\n", "\n")
            .Replace("\\\\", "\\");
    }

    private static string FindYtDlp()
    {
        string local = Path.Combine(BaseDir, "yt-dlp.exe");
        if (File.Exists(local)) return local;
        string path = Environment.GetEnvironmentVariable("PATH");
        if (path != null) {
            foreach (string dir in path.Split(Path.PathSeparator)) {
                try {
                    string candidate = Path.Combine(dir.Trim(), "yt-dlp.exe");
                    if (File.Exists(candidate)) return candidate;
                } catch { }
            }
        }
        return "yt-dlp.exe"; // let CreateProcess search PATH at spawn time
    }

    private static string ConfigArgs()
    {
        string cfg = Path.Combine(BaseDir, "foxygestures_ytdlp.args");
        var sb = new StringBuilder();
        try {
            foreach (string line in File.ReadAllLines(cfg)) {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#")) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(t.IndexOf(' ') >= 0 ? "\"" + t + "\"" : t);
            }
        } catch { }
        return sb.ToString();
    }

    private static void SendJson(Stream stdout, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] len = BitConverter.GetBytes(body.Length);
        stdout.Write(len, 0, 4);
        stdout.Write(body, 0, body.Length);
        stdout.Flush();
    }

    private static string HandleRequest(string json)
    {
        string url = JsonString(json, "mediaUrl");
        if (string.IsNullOrEmpty(url)) url = JsonString(json, "url");
        string referer = JsonString(json, "referer");
        if (string.IsNullOrEmpty(url)) return "{\"ok\":false,\"error\":\"no url in request\"}";

        // Defang quotes so a crafted URL cannot break out of its argument.
        url = url.Replace("\"", "");
        if (referer != null) referer = referer.Replace("\"", "");

        var args = new StringBuilder();
        string cfg = ConfigArgs();
        if (cfg.Length > 0) args.Append(cfg).Append(' ');
        if (!string.IsNullOrEmpty(referer)) args.Append("--referer \"").Append(referer).Append("\" ");
        args.Append("-- \"").Append(url).Append("\"");

        string exe = FindYtDlp();
        Log("spawn: " + exe + " " + args);
        try {
            var psi = new ProcessStartInfo(exe, args.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);
            lastChild = p;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) {
                if (e.Data != null) Log("yt-dlp: " + e.Data);
            };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) {
                if (e.Data != null) Log("yt-dlp! " + e.Data);
            };
            p.EnableRaisingEvents = true;
            p.Exited += delegate(object s, EventArgs e) {
                Log("yt-dlp exited, code " + p.ExitCode);
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return "{\"ok\":true,\"pid\":" + p.Id + "}";
        } catch (Exception ex) {
            Log("spawn failed: " + ex);
            return "{\"ok\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}";
        }
    }

    public static int Main()
    {
        Stream stdin = Console.OpenStandardInput();
        Stream stdout = Console.OpenStandardOutput();
        while (true) {
            byte[] lenBuf = new byte[4];
            if (ReadFully(stdin, lenBuf, 4) < 4) break; // browser closed the pipe
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0 || len > 1048576) return 1;
            byte[] buf = new byte[len];
            if (ReadFully(stdin, buf, len) < len) break;
            string json = Encoding.UTF8.GetString(buf);
            Log("request: " + json);
            SendJson(stdout, HandleRequest(json));
        }
        // The browser closes the pipe after the reply. Keep the process alive until yt-dlp
        // exits so its output keeps draining into the log instead of hitting a broken pipe.
        try { if (lastChild != null && !lastChild.HasExited) lastChild.WaitForExit(); } catch { }
        return 0;
    }
}
