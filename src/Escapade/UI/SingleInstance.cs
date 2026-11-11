using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Escapade.Core;
using static Escapade.Interop.NativeMethods;

namespace Escapade.UI;

/// <summary>
/// One Escapade per user session: "one window, never many". The first instance holds a named
/// mutex and listens on a named pipe; a later launch sends it its working folder and command-line
/// paths, which open as tabs, and exits. Both are scoped to the current user and logon session.
/// </summary>
internal static class SingleInstance
{
    private const int MaxMessageChars = 64 * 1024;

    private static string Id
    {
        get
        {
            var raw = $"Escapade-{Environment.UserDomainName}-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";
            return new string(raw.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
        }
    }

    public static string MutexName => @"Local\" + Id;

    /// <summary>Hands the arguments to the running instance. False if it couldn't be reached.</summary>
    public static bool TrySend(string[] args)
    {
        var message = SettingsFile.Serialize(new[] { Environment.CurrentDirectory }.Concat(args).ToArray());
        AllowSetForegroundWindow(ASFW_ANY); // so it may come to the front: we were just launched, so we may grant it
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                pipe.Connect(1000);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
                writer.Write(message);
                writer.Flush();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // Probably still starting up and not listening yet.
            }
            if (DateTime.UtcNow > deadline)
                return false;
            Thread.Sleep(200);
        }
    }

    /// <summary>
    /// Listens for later launches on a background thread. <paramref name="received"/> gets the
    /// sender's working folder followed by its arguments, on the UI thread.
    /// </summary>
    public static void Listen(Control ui, Action<string, string[]> received, CancellationToken token)
    {
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var buffer = new char[MaxMessageChars];
                    int length = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (SettingsFile.Deserialize<string[]>(new string(buffer, 0, length)) is [var cwd, .. var args])
                        ui.BeginInvoke(() => received(cwd, args));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException
                                           or UnauthorizedAccessException or ObjectDisposedException)
                {
                    // A garbled message or a client that went away; keep listening.
                    Thread.Sleep(100);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Escapade single instance",
        };
        thread.Start();
    }
}
