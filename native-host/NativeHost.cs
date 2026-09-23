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
        string v = m.Groups[1].Value;
        v = Regex.Replace(v, "\\\\u([0-9a-fA-F]{4})",
            mm => ((char)int.Parse(mm.Groups[1].Value, System.Globalization.NumberStyles.HexNumber)).ToString());
        return v
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
        string referer = JsonString(json, "referer");
        string title = JsonString(json, "title");

        // Defang quotes so a crafted value cannot break out of its argument.
        if (mediaUrl != null) mediaUrl = mediaUrl.Replace("\"", "");
        if (pageUrl != null) pageUrl = pageUrl.Replace("\"", "");
        if (referer != null) referer = referer.Replace("\"", "");
        if (string.IsNullOrEmpty(mediaUrl) && string.IsNullOrEmpty(pageUrl))
            return "{\"ok\":false,\"error\":\"no url in request\"}";
        if (string.IsNullOrEmpty(pageUrl) || pageUrl == mediaUrl) pageUrl = null;
        if (string.IsNullOrEmpty(mediaUrl)) mediaUrl = null;

        var common = new StringBuilder();
        string cfg = ConfigArgs();
        if (cfg.Length > 0) common.Append(cfg).Append(' ');
        if (!string.IsNullOrEmpty(referer)) common.Append("--referer \"").Append(referer).Append("\" ");

        // A bare manifest URL goes through yt-dlp's generic extractor: it names the output
        // after the URL slug ("video [video].mp4") and, for sites that split audio into a
        // separate HLS rendition (Twitter, Bluesky), silently downloads video-only. The
        // page URL's site extractor assembles the full A/V stream properly — so try it
        // first and fall back to the captured manifest only if extraction fails (the
        // fallback is what rescues posts hidden from logged-out viewers).
        string manifestArgs = common.ToString();
        if (mediaUrl != null && !string.IsNullOrEmpty(title)) {
            string safe = SanitizeTitle(title);
            if (safe.Length > 0) manifestArgs += "-o \"" + safe + " [%(id)s].%(ext)s\" ";
        }

        string exe = FindYtDlp();
        Log("request: page=" + (pageUrl ?? "-") + "  manifest=" + (mediaUrl ?? "-") +
            (string.IsNullOrEmpty(title) ? "" : "  title: " + title));

        // Wrapper script: doubling % protects percent-encoded URLs and the %(id)s template
        // from batch expansion. cmd parses batch files in the OEM codepage; chcp 65001
        // makes it read this UTF-8 file correctly (an em-dash otherwise becomes "ΓÇö").
        string cmdPath = Path.Combine(Path.GetTempPath(),
            "fgytdlp-" + Guid.NewGuid().ToString("N") + ".cmd");
        var batch = new StringBuilder("@echo off\r\nchcp 65001 >nul\r\n");
        if (pageUrl != null) {
            batch.Append('\"').Append(exe).Append("\" ").Append(common.ToString().Replace("%", "%%"))
                .Append("-- \"").Append(pageUrl.Replace("%", "%%")).Append("\"")
                .Append(" >> \"").Append(LogPath).Append("\" 2>&1\r\n");
        }
        if (mediaUrl != null) {
            string manifestLine = '\"' + exe + "\" " + manifestArgs.Replace("%", "%%") +
                "-- \"" + mediaUrl.Replace("%", "%%") + "\" >> \"" + LogPath + "\" 2>&1";
            if (pageUrl != null) {
                batch.Append("if errorlevel 1 (\r\n")
                    .Append("  echo %date% %time%  page-URL extraction failed, retrying captured manifest >> \"")
                    .Append(LogPath).Append("\"\r\n  ").Append(manifestLine).Append("\r\n)\r\n");
            } else {
                batch.Append(manifestLine).Append("\r\n");
            }
        }
        batch.Append("echo %date% %time%  yt-dlp exited, code %errorlevel% >> \"")
            .Append(LogPath).Append("\"\r\n");
        try {
            File.WriteAllText(cmdPath, batch.ToString());
        } catch (Exception ex) {
            return "{\"ok\":false,\"error\":\"wrapper write failed: " + ex.Message.Replace("\"", "'") + "\"}";
        }
        Log("wrapper: " + cmdPath);

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
