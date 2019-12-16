namespace csclip
{
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

        // Supported jsonrpc method:
        // Sent:
        // - :paste: <text data> -> nil
        // - :get: <data format> -> <requested data>
        // Received:
        // - :copy:  [{cf:, data:}+] -> nil
        // - :get: <data format> -> <requested data>
        [Verb("server", HelpText = "Interactively get/put data to clipboard. Using jsonrpc")]
        class ServerOptions
        {
            [Option('h', "host", Default = "0.0.0.0", HelpText = "Tcp host.")]
            public string Host { get; set; }
            [Option('p', "port", Default = 9123, HelpText = "Tcp port.")]
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
            else
            {
                return format;
            }
        }

        struct ClipboardData
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

        async void ClipboardContentChangedHandler(object sender, object e)
        {
            try
            {
                // need delay to make sure data is available in clipboard
                await Task.Delay(500);
                var data = await GetDataAsync("text");
                foreach (var requester in m_requesters)
                {
                    await (requester.Value?.PasteDataAsync(data) ?? Task.CompletedTask);
                }
            }
            catch (Exception ex) { }
        }

        async void OnDeferredDataRequestHandler(DataProviderRequest request)
        {
            var deferral = request.GetDeferral();
            try
            {
                var clipboardFormat = StandardToCfFormat(request.FormatId);
                var data = await (m_requesters?[m_lastCopyId]?.GetDataAsync(clipboardFormat)
                    ?? Task.FromResult<string>(null));

                // Get and put data in request
                var norm = NormalizeClipboardData(new ClipboardData { cf = clipboardFormat, data = data });
                if (norm.cf == request.FormatId)
                {
                    request.SetData(norm.data);
                }
            }
            catch (Exception) { }
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
                    catch (Exception) { }
                    await Task.Delay(100);
                }

                m_lastCopyId = id;
            });
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

        async Task<string> GetDataAsync(string format)
        {
            var data = await m_mainThread.InvokeAsync(() =>
            {
                return Clipboard.GetContent();
            });

            var standardFormat = CfToStandardFormat(format);

            try
            {
                if (data.Contains(format))
                {
                    return (string)await data.GetDataAsync(format); ;
                }
            }
            catch (Exception) { }

            return "";
        }

        async Task ExecutePasteAsync(PasteOptions opts)
        {
            var data = await GetDataAsync(opts.Format);
            Console.Write(data);
        }

        public interface IRequest
        {
            // Sent:
            // - paste: <text data> -> nil
            // - get: <data format> -> <requested data>
            [JsonRpcMethod("paste")]
            Task PasteDataAsync(string text);

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
                        catch (Exception) { }
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
        private int m_lastCopyId = -1;

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
                Clipboard.Flush();
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
