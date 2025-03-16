using CommandLine;
using System.Text.Json;
using StreamJsonRpc;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Drawing.Imaging;
using System.Drawing;

namespace csclip
{
    public class App
    {

        [Verb("copy", HelpText = "Copy to clipboard through pipe using clipboard data format {\"cf\":, \"data\":}")]
        class CopyOptions { }

        [Verb("paste", HelpText = "Get content from clipboard")]
        class PasteOptions
        {
            [Option('f', "format", Default = "text", HelpText = "Clipboard format. Supported format: <text|html>.")]
            public string Format { get; set; }
        }

        [Verb("server", HelpText = "Interactively get/put data to clipboard. Using jsonrpc")]
        class ServerOptions
        {
            [Option('h', "host", Default = "0.0.0.0", HelpText = "Tcp host")]
            public string Host { get; set; }
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

        public struct ClipboardData
        {
            public string cf; // Clipboard format
            public string data;
        }

        static ClipboardData NormalizeClipboardData(ClipboardData org)
        {
            var norm = new ClipboardData();
            switch (org.cf)
            {
                case "text":
                    norm.cf = StandardDataFormats.Text;
                    norm.data = org.data;
                    break;
                case "html":
                    norm.cf = StandardDataFormats.Html;
                    norm.data = (org.data == null) ? null : HtmlFormatHelper.CreateHtmlFormat(org.data);
                    break;
                case "bitmap":
                    norm.cf = StandardDataFormats.Bitmap;
                    norm.data = org.data;
                    break;
                default:
                    return org;
            }

            return norm;
        }

        public App()
        {
            Clipboard.ContentChanged += ClipboardContentChangedHandler;
        }

        async Task<bool> CheckDataFormatAsync(string format)
        {
            await _dispatcher.Resume();
            var data = Clipboard.GetContent();
            return data.Contains(format);
        }

        void ClipboardContentChangedHandler(object sender, object e)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                var cf = (await CheckDataFormatAsync(StandardDataFormats.Bitmap)) ? "bitmap" : "text";
                var data = (cf == "bitmap") ? null : await GetDataAsync(cf);
                foreach (var requester in _requesters)
                {
                    await (requester.Value?.PasteDataAsync(new ClipboardData { cf = cf, data = data }) ?? Task.CompletedTask);
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
                    var norm = NormalizeClipboardData(new ClipboardData { cf = clipboardFormat, data = data });
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

        async Task CopyAsync(int id, IList<ClipboardData> data)
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

            await _dispatcher.Resume();
            // Need this https://stackoverflow.com/questions/68666/clipbrd-e-cant-open-error-when-setting-the-clipboard-from-net
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    Clipboard.SetContent(package);
                    break;
                }
                catch { }
                await Task.Delay(100);
            }

            _lastReqId = id;
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
            public string mime;
        }

        async Task<string> GetDataToFileAsync(SaveDataToFileOptions options)
        {
            await _dispatcher.Resume();
            var data = Clipboard.GetContent();

            // Ensure we're not blocking the UI thread
            await Task.Delay(0).ConfigureAwait(false);
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
                        var sblob = await (blob as RandomAccessStreamReference).OpenReadAsync();
                        var contentType = options.mime ?? sblob.ContentType;
                        var ext = MimeTypes.MimeTypeMap.GetExtension(contentType);
                        var fileName = $"{options.prefix}{Guid.NewGuid()}{ext}";
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var tempFile = await StorageFile.CreateStreamedFileAsync("temp", (req) =>
                                {
                                    using (var image = Image.FromStream(sblob.AsStream()))
                                    using (var outputStream = req.AsStreamForWrite())
                                    {
                                        var encoderParameters = new EncoderParameters(1);
                                        encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, 100L);
                                        var codec = GetEncoder(MimeToImageFormat[contentType]);
                                        image.Save(outputStream, codec, encoderParameters);
                                    }

                                }, null);

                                var path = Path.GetFullPath($"{options.path}/");
                                // Ensure the directory exists
                                (new FileInfo(path)).Directory.Create();
                                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                                await tempFile.CopyAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
                            }
                            catch (Exception) { }
                        });

                        return fileName;
                    }
                    else if (blob is string)
                    {
                        return blob as string;
                    }
                }
            }

            return string.Empty;
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }

        async Task ExecuteCopyAsync()
        {
            List<ClipboardData> data;

            var text = await Console.In.ReadToEndAsync();
            try
            {
                switch (text[0])
                {
                    case '[':
                        data = JsonSerializer.Deserialize<List<ClipboardData>>(text);
                        break;
                    default:
                        data = [JsonSerializer.Deserialize<ClipboardData>(text)];
                        break;
                }
            }
            catch (JsonException)
            {
                data = [new ClipboardData { cf = "text", data = text }];
            }

            await CopyAsync(-1, data);
        }

        async Task ExecutePasteAsync(PasteOptions opts)
        {
            var data = await GetDataAsync(opts.Format);
            Console.Write(data);
        }

        public interface IRequest : IDisposable
        {
            // Sent:
            // - paste: {cf:, data:} -> nil
            // - get: <data format> -> <requested data>
            [JsonRpcMethod("paste")]
            Task PasteDataAsync(ClipboardData text);

            [JsonRpcMethod("get")]
            Task<string> GetDataAsync(string format);
        }

        class Responser
        {
            private App _app = null;
            private int _id = 0;
            public Responser(int id, App app)
            {
                _id = id;
                _app = app;
            }

            // Received:
            // - :copy:  [{cf:, data:}+] -> nil
            // - :get: <data format> -> <requested data>
            // - :get-to-file: {cf:, path:} -> <saved file name>
            [JsonRpcMethod("copy")]
            public async Task HandleCopyDataAsync(IList<ClipboardData> data)
            {
                await _app.CopyAsync(_id, data);
            }

            [JsonRpcMethod("get")]
            public async Task<string> HandleGetDataAsync(string format)
            {
                return await _app.GetDataAsync(format);
            }

            [JsonRpcMethod("get-to-file")]
            public async Task<string> HandleGetDataToFileAsync(SaveDataToFileOptions options)
            {
                return await _app.GetDataToFileAsync(options);
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
                        var rpc = new JsonRpc(conn.GetStream());
                        using (conn)
                        using (var requester = rpc.Attach<IRequest>())
                        {
                            var id = Interlocked.Increment(ref Counter);
                            try
                            {
                                _requesters[id] = requester;

                                rpc.AddLocalRpcTarget(new Responser(id, this));

                                // Initiate JSON-RPC message processing.
                                rpc.StartListening();

                                await rpc.Completion;
                            }
                            catch { }
                            finally
                            {
                                _requesters.TryRemove(id, out _);
                                if (_requesters.IsEmpty)
                                {
                                    _listener.Stop();
                                }
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

        private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
        private TcpListener _listener = null;
        private ConcurrentDictionary<int, IRequest> _requesters;
        private int _lastReqId = -1;

        private static int Counter = -1;
        private static readonly Dictionary<string, ImageFormat> MimeToImageFormat = new Dictionary<string, ImageFormat>
        { 
            // Images
            { "image/bmp", ImageFormat.Bmp },
            { "image/emf", ImageFormat.Emf },
            { "image/exif", ImageFormat.Exif },
            { "image/gif", ImageFormat.Gif },
            { "image/jpeg", ImageFormat.Jpeg },
            { "image/png", ImageFormat.Png },
            { "image/tiff", ImageFormat.Tiff },
            { "image/wmf", ImageFormat.Wmf },
            { "image/x-citrix-jpeg", ImageFormat.Jpeg },
            { "image/x-citrix-png", ImageFormat.Png },
            { "image/x-icon", ImageFormat.Icon }, 
            { "image/x-png", ImageFormat.Png },
        };


        public async Task RunAsync(string[] args)
        {
            try
            {
                await Parser.Default.ParseArguments<CopyOptions, PasteOptions, ServerOptions>(args)
                       .MapResult(
                            (CopyOptions _) => ExecuteCopyAsync(),
                            (PasteOptions opts) => ExecutePasteAsync(opts),
                            (ServerOptions opts) => ExecuteServerAsync(opts),
                            errs => Task.FromResult(0));

                await _dispatcher.Resume();
                try
                {
                    Clipboard.Flush();
                }
                catch
                { }
            }
            catch (Exception e)
            {
                await Console.Error.WriteLineAsync(e.Message);
            }
            finally
            {
                _dispatcher.InvokeShutdown();
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            var app = new App();
            _ = app.RunAsync(args);

            try
            {
                // Message pump
                Dispatcher.Run();
            }
            catch { }
        }

    }
}
