using CommandLine;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Windows.Threading;
using System.Text.Json;

namespace csclip
{
    public partial class App
    {

        [Verb("copy", HelpText = "Copy to clipboard through pipe using clipboard data format {\"cf\":, \"data\":}")]
        class CopyOptions { }

        [Verb("paste", HelpText = "Get content from clipboard")]
        class PasteOptions
        {
            [Option('f', "format", Default = "text", HelpText = "Clipboard format. Supported format: <text|html>.")]
            public required string Format { get; set; }
        }

        [Verb("server", HelpText = "Interactively get/put data to clipboard. Using jsonrpc")]
        class ServerOptions
        {
            [Option('h', "host", Default = "0.0.0.0", HelpText = "Tcp host")]
            public required string Host { get; set; }
            [Option('p', "port", Default = 9123, HelpText = "Tcp port")]
            public int Port { get; set; }
        }

        static string CfToStandardFormat(string format)
        {
            switch (format)
            {
                case "text":
                    return StandardDataFormats.Text;
                case "html":
                    return StandardDataFormats.Html;
                case "bitmap":
                    return StandardDataFormats.Bitmap;
                default:
                    return format;
            }
        }

        static string StandardToCfFormat(string format)
        {
            if (format == StandardDataFormats.Text)
            {
                return "text";
            }
            else if (format == StandardDataFormats.Html)
            {
                return "html";
            }
            else if (format == StandardDataFormats.Bitmap)
            {
                return "bitmap";
            }

            return format;
        }

        public record ClipboardData(string cf, string? data);

        // ClipboardData source generator context
        [JsonSerializable(typeof(ClipboardData))]
        [JsonSerializable(typeof(List<ClipboardData>))]
        internal partial class SrcGenCtx : JsonSerializerContext
        {
        }


        static ClipboardData NormalizeClipboardData(ClipboardData org)
        {
            switch (org.cf)
            {
                case "text":
                    return new ClipboardData(
                        cf: StandardDataFormats.Text,
                        data: (org.data == null) ? string.Empty : HtmlFormatHelper.CreateHtmlFormat(org.data));
                case "html":
                    return new ClipboardData(
                        cf: StandardDataFormats.Html,
                        data: (org.data == null) ? string.Empty : HtmlFormatHelper.CreateHtmlFormat(org.data));
                case "bitmap":
                    return new ClipboardData(
                        cf: StandardDataFormats.Bitmap,
                        data: org.data);
                default:
                    return org;
            }
        }

        public App()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            Clipboard.ContentChanged += ClipboardContentChangedHandler;
        }

        async Task<bool> CheckDataFormatAsync(string format)
        {
            var data = await _dispatcher.InvokeAsync(() =>
            {
                return Clipboard.GetContent();
            });

            return data.Contains(format);
        }

        void ClipboardContentChangedHandler(object? sender, object? e)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                var cf = (await CheckDataFormatAsync(StandardDataFormats.Bitmap)) ? "bitmap" : "text";
                var data = (cf == "bitmap") ? null : await GetDataAsync(cf);
                foreach (var requester in _requesters)
                {
                    await (requester.Value?.PasteDataAsync(new ClipboardData(cf, data)) ?? Task.CompletedTask);
                }
            });
        }

        void OnDeferredDataRequestHandler(DataProviderRequest request)
        {
            _ = Task.Run(async () =>
            {
                var deferral = request.GetDeferral();
                try
                {
                    var clipboardFormat = StandardToCfFormat(request.FormatId);
                    var data = await (_requesters?[_lastReqId]?.GetDataAsync(clipboardFormat) ??
                                      Task.FromResult<string>(string.Empty));

                    // Get and put data in request
                    var norm = NormalizeClipboardData(new ClipboardData(cf: clipboardFormat, data));
                    if (norm.cf == request.FormatId)
                    {
                        request.SetData(norm.data);
                    }
                }
                catch { }
                finally
                {
                    deferral.Complete();
                }
            });
        }

        async Task CopyAsync(int id, IList<ClipboardData>? data)
        {
            var package = new DataPackage();
            if (data == null)
            {
                return;
            }

            foreach (var d in data)
            {
                var norm = NormalizeClipboardData(d);
                if (d.data != null)
                {
                    package.SetData(norm.cf, norm.data);
                }
                else
                {
                    package.SetDataProvider(norm.cf, OnDeferredDataRequestHandler);
                }
            }

            await _dispatcher.InvokeAsync(async () =>
            {
                // Need this https://stackoverflow.com/questions/68666/clipbrd-e-cant-open-error-when-setting-the-clipboard-from-net
                const int retry = 10;
                for (int i = 0; i < retry; i++)
                {
                    try
                    {
                        Clipboard.SetContent(package);
                        break;
                    }
                    catch {}
                    await Task.Delay(100);
                }

                _lastReqId = id;
            });
        }

        async Task<string> GetDataAsync(string format)
        {
            return await GetDataToFileAsync(new SaveDataToFileOptions { cf = format });
        }

        public struct SaveDataToFileOptions
        {
            public string cf;
            public string path;
            public string prefix;
        }

        async Task<string> GetDataToFileAsync(SaveDataToFileOptions options)
        {
            var data = await _dispatcher.InvokeAsync(() =>
            {
                return Clipboard.GetContent();
            });

            var stdFormat = CfToStandardFormat(options.cf);

            if (data.Contains(stdFormat))
            {
                if (options.path == null)
                {
                    return (string)await data.GetDataAsync(stdFormat); ;
                }
                else
                {
                    var blob = await data.GetDataAsync(stdFormat);
                    if (blob is RandomAccessStreamReference)
                    {
                        var sblob = await (blob as RandomAccessStreamReference)?.OpenReadAsync();
                        var ext = MimeTypes.MimeTypeMap.GetExtension(sblob.ContentType);
                        var fileName = $"{options.prefix}{Guid.NewGuid()}{ext}";
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var tempFile = await StorageFile.CreateStreamedFileAsync("temp", (sout) =>
                                {
                                    _ = Task.Run(async () =>
                                    {
                                        // Copy stream to file
                                        await RandomAccessStream.CopyAndCloseAsync(sblob, sout);
                                    });
                                }, null);

                                var path = Path.GetFullPath($"{options.path}/");
                                // Ensure the directory exists
                                (new FileInfo(path)).Directory?.Create();
                                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                                await tempFile.CopyAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
                            }
                            catch { }
                        });

                        return fileName;
                    }
                    else if (blob is string)
                    {
                        return blob as string ?? string.Empty;
                    }
                }
            }

            return string.Empty;
        }

        async Task ExecuteCopyAsync()
        {
            List<ClipboardData>? data;

            var text = await Console.In.ReadToEndAsync();
            try
            {
                switch (text[0])
                {
                    case '[':
                        data = JsonSerializer.Deserialize<List<ClipboardData>>(text, SrcGenCtx.Default.ListClipboardData);
                        break;
                    default:
                        data = [JsonSerializer.Deserialize<ClipboardData>(text, SrcGenCtx.Default.ClipboardData)];
                        break;
                }
            }
            catch (JsonException)
            {
                data = [new ClipboardData(cf: "text", data: text)];
            }

            await CopyAsync(-1, data);
        }

        async Task ExecutePasteAsync(PasteOptions opts)
        {
            var data = await GetDataAsync(opts.Format);
            Console.Write(data);
        }

        public interface IRequest
        {
            // Sent:
            // - paste: {cf:, data:} -> nil
            // - get: <data format> -> <requested data>
            [JsonRpcMethod("paste")]
            Task PasteDataAsync(ClipboardData text);

            [JsonRpcMethod("get")]
            Task<string> GetDataAsync(string format);
        }

        class Responser(int id, App prog)
        {

            // Received:
            // - :copy:  [{cf:, data:}+] -> nil
            // - :get: <data format> -> <requested data>
            // - :get-to-file: {cf:, path:} -> <saved file name>
            [JsonRpcMethod("copy")]
            public async Task HandleCopyDataAsync(IList<ClipboardData> data)
            {
                await prog.CopyAsync(id, data);
            }

            [JsonRpcMethod("get")]
            public async Task<string> HandleGetDataAsync(string format)
            {
                return await prog.GetDataAsync(format);
            }

            [JsonRpcMethod("get-to-file")]
            public async Task<string> HandleGetDataToFileAsync(SaveDataToFileOptions options)
            {
                return await prog.GetDataToFileAsync(options);
            }
        }

        async Task ExecuteServerAsync(ServerOptions opts)
        {
            _requesters = new ConcurrentDictionary<int, IRequest>();
            _listener = new TcpListener(IPAddress.Parse(opts.Host), opts.Port);
            _listener.Start();

            while (true)
            {
                try
                {
                    var conn = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        int id = Interlocked.Increment(ref s_counter);
                        try
                        {
                            var rpc = new JsonRpc(conn.GetStream());
                            _requesters[id] = rpc.Attach<IRequest>();

                            rpc.AddLocalRpcTarget(new Responser(id, this));

                            // Initiate JSON-RPC message processing.
                            rpc.StartListening();

                            await rpc.Completion;
                        }
                        catch {}
                        finally
                        {
                            if (_requesters.TryRemove(id, out IRequest? requester))
                            {
                                conn.Close();

                                if (_requesters.IsEmpty)
                                {
                                    _listener.Stop();
                                }

                                ((IDisposable)requester)?.Dispose();
                            }
                        }
                    });
                }
                catch (ObjectDisposedException)
                {
                    // This happens when Stop is called and we're waiting for connection.
                    break;
                }
            }
        }

        private readonly Dispatcher _dispatcher = null!;
        private TcpListener _listener = null!;
        private ConcurrentDictionary<int, IRequest> _requesters = null!;
        private int _lastReqId = -1;

        private static int s_counter = -1;

        public async Task RunAsync(string[] args)
        {
            await Parser.Default.ParseArguments<CopyOptions, PasteOptions, ServerOptions>(args)
                   .MapResult(
                        (CopyOptions _) => ExecuteCopyAsync(),
                        (PasteOptions opts) => ExecutePasteAsync(opts),
                        (ServerOptions opts) => ExecuteServerAsync(opts),
                        errs => Task.FromResult(0));

            await _dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Clipboard.Flush();
                }
                catch {}
            });
        }

        [STAThread]
        static void Main(string[] args)
        {
            var app = new App();
            _ = Task.Run(async () =>
            {
                try
                {
                    await app.RunAsync(args);
                }
                catch (Exception e)
                {
                    await Console.Error.WriteLineAsync(e.Message);
                }
                finally
                {
                    Environment.Exit(0);
                }

            });

            // Message pump
            Dispatcher.Run();

        }

    }
}
