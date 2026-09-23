// Foxy Gestures yt-dlp native messaging host.
//
// Speaks the Firefox native messaging protocol on stdio (4-byte little-endian length
// prefix + UTF-8 JSON). For each request {url, mediaUrl, referer} it launches yt-dlp with
// the mediaUrl when present (an HLS/DASH manifest captured from the page) else the page
// URL, and replies {"ok":true,"pid":N}.
//
// Why the launch goes through WMI: Firefox runs native messaging hosts inside a Windows
// Job Object configured kill-on-close, and tears the job down seconds after the reply.
// A plain Process.Start child dies with the job (observed: yt-dlp frozen mid-download,
// .part file stranded). Win32_Process.Create children are parented to the WMI service,
// outside the job, so they survive the host's teardown. The yt-dlp command line is written
// to a temporary .cmd wrapper so its output can be appended to foxygestures_ytdlp.log
// without inheriting any of the host's pipe handles.
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

    // Remove wrapper scripts left behind by earlier runs.
    private static void CleanTempWrappers()
    {
        try {
            foreach (string f in Directory.GetFiles(Path.GetTempPath(), "fgytdlp-*.cmd")) {
                try {
                    if (File.GetLastWriteTime(f) < DateTime.Now.AddHours(-2)) File.Delete(f);
                } catch { }
            }
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

    // Make a page title safe as a literal in a yt-dlp -o template and a Windows filename:
    // strip path-invalid characters and quotes, collapse whitespace, cap the length so
    // " [id].ext" plus the download path stay well under MAX_PATH.
    private static string SanitizeTitle(string title)
    {
        string t = Regex.Replace(title, "[<>:\"/\\\\|?*\\x00-\\x1f]", " ");
        t = Regex.Replace(t, "\\s+", " ").Trim().TrimEnd('.');
        if (t.Length > 100) t = t.Substring(0, 100).TrimEnd();
        return t;
    }

    private static string HandleRequest(string json)
    {
        string mediaUrl = JsonString(json, "mediaUrl");
        string pageUrl = JsonString(json, "url");
        string url = !string.IsNullOrEmpty(mediaUrl) ? mediaUrl : pageUrl;
        string referer = JsonString(json, "referer");
        string title = JsonString(json, "title");
        if (string.IsNullOrEmpty(url)) return "{\"ok\":false,\"error\":\"no url in request\"}";

        // Defang quotes so a crafted value cannot break out of its argument.
        url = url.Replace("\"", "");
        if (referer != null) referer = referer.Replace("\"", "");

        var args = new StringBuilder();
        string cfg = ConfigArgs();
        if (cfg.Length > 0) args.Append(cfg).Append(' ');
        if (!string.IsNullOrEmpty(referer)) args.Append("--referer \"").Append(referer).Append("\" ");
        // A direct manifest URL goes through yt-dlp's generic extractor, which names the
        // output after the URL slug ("video [video].mp4"). Use the tab title instead.
        // Page-URL handoffs keep the site extractor's own richer naming.
        if (!string.IsNullOrEmpty(mediaUrl) && !string.IsNullOrEmpty(title)) {
            string safe = SanitizeTitle(title);
            if (safe.Length > 0) args.Append("-o \"").Append(safe).Append(" [%(id)s].%(ext)s\" ");
        }
        args.Append("-- \"").Append(url).Append("\"");

        string exe = FindYtDlp();
        Log("request: " + url + (string.IsNullOrEmpty(title) ? "" : "  title: " + title));

        // Wrapper script: doubling % protects percent-encoded URLs from batch expansion.
        // The exit-code line gives the log a definitive end-of-download marker even though
        // this host may already be gone by then.
        string cmdPath = Path.Combine(Path.GetTempPath(),
            "fgytdlp-" + Guid.NewGuid().ToString("N") + ".cmd");
        string batch =
            "@echo off\r\n" +
            "\"" + exe + "\" " + args.ToString().Replace("%", "%%") +
                " >> \"" + LogPath + "\" 2>&1\r\n" +
            "echo %date% %time%  yt-dlp exited, code %errorlevel% >> \"" + LogPath + "\"\r\n";
        try {
            File.WriteAllText(cmdPath, batch);
        } catch (Exception ex) {
            return "{\"ok\":false,\"error\":\"wrapper write failed: " + ex.Message.Replace("\"", "'") + "\"}";
        }
        Log("wrapper: " + cmdPath + "  spawn: " + exe + " " + args);

        // Create the process via WMI so it is parented outside Firefox's kill-on-close job.
        // PowerShell exits as soon as the create returns, so waiting for it guarantees the
        // download exists before we reply (and therefore before any job teardown).
        string ps =
            "$s = New-CimInstance -ClassName Win32_ProcessStartup " +
              "-Property @{ShowWindow=[uint16]0} -ClientOnly; " +
            "$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create " +
              "-Arguments @{CommandLine='cmd.exe /c \"" + cmdPath + "\"'; ProcessStartupInformation=$s}; " +
            "Write-Output ($r.ReturnValue.ToString() + ':' + $r.ProcessId.ToString())";
        try {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -WindowStyle Hidden -Command \"" + ps.Replace("\"", "\\\"") + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = Process.Start(psi);
            lastChild = p;
            string outp = p.StandardOutput.ReadToEnd().Trim();
            string errp = p.StandardError.ReadToEnd().Trim();
            p.WaitForExit(30000);
            if (errp.Length > 0) Log("powershell: " + errp);
            Match m = Regex.Match(outp, @"^(\d+):(\d+)$");
            if (m.Success && m.Groups[1].Value == "0") {
                return "{\"ok\":true,\"pid\":" + m.Groups[2].Value + "}";
            }
            return "{\"ok\":false,\"error\":\"wmi create failed: " +
                (m.Success ? "code " + m.Groups[1].Value : (outp + " " + errp).Replace("\"", "'")) + "\"}";
        } catch (Exception ex) {
            Log("wmi spawn failed: " + ex);
            return "{\"ok\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}";
        }
    }

    public static int Main()
    {
        CleanTempWrappers();
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
            SendJson(stdout, HandleRequest(json));
        }
        // The browser closes the pipe after the reply; nothing of ours outlives that except
        // the WMI-created yt-dlp, which is precisely the point.
        try { if (lastChild != null && !lastChild.HasExited) lastChild.WaitForExit(5000); } catch { }
        return 0;
    }
}
