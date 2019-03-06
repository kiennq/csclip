using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommandLine;
using Newtonsoft.Json;
using Windows.ApplicationModel.DataTransfer;
using System.Windows.Threading;
using System.Threading;
using System.Runtime.InteropServices;

namespace csclip
{
    class RunInMainThread
    {
        public RunInMainThread(TaskScheduler taskScheduler)
        {
            m_taskScheduler = taskScheduler;
        }
        public Task<T> Invoke<T>(Func<T> func)
        {
            return Task.Run(() => { }).ContinueWith((_) =>
            {
                return func();
            }, m_taskScheduler);
        }

        public Task Invoke(Action func)
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

        [Verb(c_commandCopy, HelpText = "Copy to clipboard through pipe using clipboard data format {\"cf\":, \"data\":}")]
        class CopyOptions { }

        [Verb(c_commandPaste, HelpText = "Get content from clipboard")]
        class PasteOptions
        {
            [Option('f', "format", Default = "text", HelpText = "Clipboard format. Supported format: <text|html>")]
            public string Format { get; set; }
            [Option('i', "rpc-format", HelpText = "Format content using rpc format <size>\\r\\n{\"data\":}")]
            public bool RPCFormat { get; set; }
        }

        [Verb("server", HelpText = "Interactively get/put data to clipboard. Data format <size>\\r\\n{\"id\":, \"command\":\"<copy|paste>\", \"data\":}")]
        class ServerOptions
        {
            [Option('e', "encode", HelpText = "Using base64 encoding for data")]
            public bool EncodeBase64 { get; set; }
        }

        static string ConvertToClipboardFormat(string format)
        {
            switch(format)
            {
                case "text":
                    return StandardDataFormats.Text;
                case "html":
                    return StandardDataFormats.Html;
                default:
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
            switch(org.cf)
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

        string ToRPCFormat(object o)
        {
            var jsonify = JsonConvert.SerializeObject(o);
            if (m_useBase64Encode)
            {
                jsonify = Convert.ToBase64String(Encoding.UTF8.GetBytes(jsonify));
            }

            return String.Format("{0}\r\n{1}", Encoding.UTF8.GetByteCount(jsonify), jsonify);
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
            // need delay to make sure data is available in clipboard
            await Task.Delay(500);
            await DoPaste(new PasteOptions { Format = "text", RPCFormat = true });
        }

        async Task DoCopy(CopyOptions opts)
        {
            var data = new List<ClipboardData>();

            var text = await Console.In.ReadToEndAsync();
            if (m_useBase64Encode)
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            }

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

            await DoCopyInternal(data);
        }

        async Task DoCopyInternal(IList<ClipboardData> data)
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
                    package.SetData(norm.cf, "1");
                    package.SetDataProvider(norm.cf, OnDeferredDataRequestHandler);
                }
            }

            await m_mainThread.Invoke(async () =>
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
            });
        }

        async void OnDeferredDataRequestHandler(DataProviderRequest request)
        {
            // TODO: Empty data indicate that this data can be defer rendered
            var deferral = request.GetDeferral();
            try
            {
                m_dataNotifier = new TaskCompletionSource<ClipboardData>();
                Console.Write(ToRPCFormat(new { id = ++m_requestId, command = c_commandGet, args = request.FormatId }));

                // Get and put data in request
                var norm = NormalizeClipboardData(await m_dataNotifier.Task);
                if (norm.cf == request.FormatId)
                {
                    request.SetData(norm.data);
                }
            }
            finally
            {
                deferral.Complete();
            }
        }

        async Task DoPaste(PasteOptions opts)
        {
            var data = await m_mainThread.Invoke(() =>
            {
                return Clipboard.GetContent();
            });

            var format = ConvertToClipboardFormat(opts.Format);

            try
            {
                if (data.Contains(format))
                {
                    var content = await data.GetDataAsync(format);
                    if (opts.RPCFormat)
                    {
                        Console.Write(ToRPCFormat(new { command = c_commandPaste, args = content }));
                    }
                    else
                    {
                        Console.Write(content);
                    }
                }
            }
            catch (Exception) { }
        }

        struct ClipboardCommand
        {
            public int id;
            public string command;
            public List<ClipboardData> data;
        }


        // Supported command format
        // Receive:
        // - copy:  <size>\r\n{command: copy, data: [{cf:, data:}+]}
        // - put:   <size>\r\n{command: put, data: [{cf:, data:}]}
        // - paste: <size>\r\n{command: paste}
        // Sent:
        // - paste: <size>\r\n{command:<paste|get>, args:}
        async Task DoRunServer(ServerOptions opts)
        {
            m_useBase64Encode = opts.EncodeBase64;
            try
            {
                Int32 dataSize = 0;
                while ((dataSize = Convert.ToInt32(await Console.In.ReadLineAsync())) > 0)
                {
                    var buffer = new char[dataSize];
                    await Console.In.ReadBlockAsync(buffer, 0, dataSize);
                    try
                    {
                        string data = new string(buffer);
                        if (opts.EncodeBase64)
                        {
                            data = Encoding.UTF8.GetString(Convert.FromBase64String(data));
                        }

                        var request = JsonConvert.DeserializeObject<ClipboardCommand>(data);
                        switch (request.command)
                        {
                            case c_commandCopy:
                                await DoCopyInternal(request.data);
                                break;
                            case c_commandPut:
                                // Delay rendering data request
                                if (request.data != null)
                                {
                                    m_dataNotifier?.TrySetResult(request.data[0]);
                                }
                                break;
                            case c_commandPaste:
                                await DoPaste(new PasteOptions { Format = "text" });
                                break;
                        }
                    }
                    catch (JsonException) { }
                }
            }
            catch (FormatException) { }
        }

        // Event to notify copy delegate about package ready
        private TaskCompletionSource<ClipboardData> m_dataNotifier = null;
        private int m_requestId = 0;
        private bool m_useBase64Encode = false;
        private const int c_bypassRequestId = 0;

        private const string c_commandPaste = "paste";
        private const string c_commandCopy = "copy";
        private const string c_commandGet = "get";
        private const string c_commandPut = "put";

        private RunInMainThread m_mainThread = null;

        public async Task RunAsync(string[] args)
        {
            await Parser.Default.ParseArguments<CopyOptions, PasteOptions, ServerOptions>(args)
                   .MapResult(
                        (CopyOptions opts) => DoCopy(opts),
                        (PasteOptions opts) => DoPaste(opts),
                        (ServerOptions opts) => DoRunServer(opts),
                        errs => Task.FromResult(0));

            await m_mainThread.Invoke(() =>
            {
                Clipboard.Flush();
            });
        }

        [STAThread]
        static void Main(string[] args)
        {
            var program = new Program();
            var dispatcher = Dispatcher.CurrentDispatcher;
            Task.Run(async () =>
            {
                try
                {
                    await program.RunAsync(args);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e.Message);
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
