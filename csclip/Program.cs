using CommandLine;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
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

namespace csclip
{
    class RunInMainThread
    {
        public RunInMainThread(TaskScheduler taskScheduler)
        {
            m_taskScheduler = taskScheduler;
        }
        public Task<T> InvokeAsync<T>(Func<T> func)
        {
            return Task.Run(() => { }).ContinueWith((_) =>
            {
                return func();
            }, m_taskScheduler);
        }

        public Task InvokeAsync(Action func)
        {
            return Task.Run(() => { }).ContinueWith((_) =>
            {
                func();
            }, m_taskScheduler);
        }

        private TaskScheduler m_taskScheduler = null;
    }

    public class Program
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

        public Program()
        {
            var context = new DispatcherSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            m_mainThread = new RunInMainThread(TaskScheduler.FromCurrentSynchronizationContext());

            Clipboard.ContentChanged += ClipboardContentChangedHandler;
        }

        async Task<bool> CheckDataFormatAsync(string format)
        {
            var data = await m_mainThread.InvokeAsync(() =>
            {
                return Clipboard.GetContent();
            });

            return data.Contains(format);
        }

        async void ClipboardContentChangedHandler(object sender, object e)
        {
            try
            {
                // need delay to make sure data is available in clipboard
                await Task.Delay(500);
                var cf = (await CheckDataFormatAsync(StandardDataFormats.Bitmap)) ? "bitmap" : "text";
                var data =  (cf == "bitmap") ? null : await GetDataAsync(cf);
                foreach (var requester in m_requesters)
                {
                    await (requester.Value?.PasteDataAsync(new ClipboardData { cf = cf, data = data}) ??
                           Task.CompletedTask);
                }
            }
            catch {}
        }

        async void OnDeferredDataRequestHandler(DataProviderRequest request)
        {
            var deferral = request.GetDeferral();
            try
            {
                var clipboardFormat = StandardToCfFormat(request.FormatId);
                var data = await (m_requesters?[m_lastReqId]?.GetDataAsync(clipboardFormat) ??
                                  Task.FromResult<string>(null));

                // Get and put data in request
                var norm = NormalizeClipboardData(new ClipboardData { cf = clipboardFormat, data = data });
                if (norm.cf == request.FormatId)
                {
                    request.SetData(norm.data);
                }
            }
            catch {}
            finally
            {
                deferral.Complete();
            }
        }

        async Task CopyAsync(int id, IList<ClipboardData> data)
        {
            var package = new DataPackage();
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

            await m_mainThread.InvokeAsync(async () =>
            {
                // Need this https://stackoverflow.com/questions/68666/clipbrd-e-cant-open-error-when-setting-the-clipboard-from-net
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        Clipboard.SetContent(package);
                        break;
                    }
                    catch {}
                    await Task.Delay(100);
                }

                m_lastReqId = id;
            });
        }

        async Task<string> GetDataAsync(string format)
        {
            return await GetDataToFileAsync(new SaveDataToFileOptions { cf = format });
        }

        public struct SaveDataToFileOptions
        {
            public string cf;
            public string store_path;
        }

        async Task<string> GetDataToFileAsync(SaveDataToFileOptions options)
        {
            var data = await m_mainThread.InvokeAsync(() =>
            {
                return Clipboard.GetContent();
            });

            var stdFormat = CfToStandardFormat(options.cf);

            if (data.Contains(stdFormat))
            {
                if (options.store_path == null)
                {
                    return (string)await data.GetDataAsync(stdFormat); ;
                }
                else
                {
                    var blob = await data.GetDataAsync(stdFormat);
                    if (blob is RandomAccessStreamReference)
                    {
                        var sblob = await (blob as RandomAccessStreamReference).OpenReadAsync();
                        var ext = MimeTypes.MimeTypeMap.GetExtension(sblob.ContentType);
                        var fileName = $"{Guid.NewGuid().ToString()}{ext}";
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var tempFile = await StorageFile.CreateStreamedFileAsync("temp", async (sout) =>
                                {
                                    await RandomAccessStream.CopyAndCloseAsync(sblob, sout);
                                }, null);

                                var path = Path.GetFullPath($"{options.store_path}/");
                                (new FileInfo(path)).Directory.Create();
                                var folder = await StorageFolder.GetFolderFromPathAsync(path);
                                await tempFile.CopyAsync(folder, fileName, NameCollisionOption.ReplaceExisting);
                            }
                            catch { }
                        });

                        return fileName;
                    }
                    else if (blob is string)
                    {
                        return blob as string;
                    }
                }
            }

            return "";
        }

        async Task ExecuteCopyAsync()
        {
            var data = new List<ClipboardData>();

            var text = await Console.In.ReadToEndAsync();
            try
            {
                switch (text[0])
                {
                    case '[':
                        data = JsonConvert.DeserializeObject<List<ClipboardData>>(text);
                        break;
                    default:
                        {
                            data.Add(JsonConvert.DeserializeObject<ClipboardData>(text));
                            break;
                        }
                }
            }
            catch (JsonException)
            {
                data.Add(new ClipboardData { cf = "text", data = text });
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

        class Responser
        {
            private Program m_owner = null;
            private int m_id = 0;
            public Responser(int id, Program prog)
            {
                m_id = id;
                m_owner = prog;
            }

            // Received:
            // - :copy:  [{cf:, data:}+] -> nil
            // - :get: <data format> -> <requested data>
            // - :get-to-file: {cf:, store_path:} -> <saved file name>
            [JsonRpcMethod("copy")]
            public async Task HandleCopyDataAsync(IList<ClipboardData> data)
            {
                await m_owner.CopyAsync(m_id, data);
            }

            [JsonRpcMethod("get")]
            public async Task<string> HandleGetDataAsync(string format)
            {
                return await m_owner.GetDataAsync(format);
            }

            [JsonRpcMethod("get-to-file")]
            public async Task<string> HandleGetDataToFileAsync(SaveDataToFileOptions options)
            {
                return await m_owner.GetDataToFileAsync(options);
            }
        }

        async Task ExecuteServerAsync(ServerOptions opts)
        {
            m_requesters = new ConcurrentDictionary<int, IRequest>();
            m_listener = new TcpListener(IPAddress.Parse(opts.Host), opts.Port);
            m_listener.Start();

            while (true)
            {
                try
                {
                    var conn = await m_listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        int id = Interlocked.Increment(ref s_counter);
                        try
                        {
                            var rpc = new JsonRpc(conn.GetStream());
                            m_requesters[id] = rpc.Attach<IRequest>();

                            rpc.AddLocalRpcTarget(new Responser(id, this));

                            // Initiate JSON-RPC message processing.
                            rpc.StartListening();

                            await rpc.Completion;
                        }
                        catch {}
                        finally
                        {
                            m_requesters.TryRemove(id, out IRequest requester);
                            conn.Close();

                            if (m_requesters.IsEmpty)
                            {
                                m_listener.Stop();
                            }

                            ((IDisposable)requester)?.Dispose();
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

        private readonly RunInMainThread m_mainThread = null;
        private TcpListener m_listener = null;
        private ConcurrentDictionary<int, IRequest> m_requesters;
        private int m_lastReqId = -1;

        private static int s_counter = -1;

        public async Task RunAsync(string[] args)
        {
            await Parser.Default.ParseArguments<CopyOptions, PasteOptions, ServerOptions>(args)
                   .MapResult(
                        (CopyOptions _) => ExecuteCopyAsync(),
                        (PasteOptions opts) => ExecutePasteAsync(opts),
                        (ServerOptions opts) => ExecuteServerAsync(opts),
                        errs => Task.FromResult(0));

            await m_mainThread.InvokeAsync(() =>
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
            var program = new Program();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = Task.Run(async () =>
              {
                  try
                  {
                      await program.RunAsync(args);
                  }
                  catch (Exception e)
                  {
                      await Console.Error.WriteLineAsync(e.Message);
                  }
                  finally
                  {
                      dispatcher.InvokeShutdown();
                      Dispatcher.ExitAllFrames();
                  }

              });

            // Message pump
            Dispatcher.Run();

        }

    }
}
