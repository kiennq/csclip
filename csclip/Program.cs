using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Text;
using Newtonsoft.Json;
using Windows.ApplicationModel.DataTransfer;
using Application = System.Windows.Forms.Application;

namespace csclip
{
    public class Program
    {

        [Verb("copy", HelpText = "Copy to clipboard through pipe using clipboard data format {\"cf\":, \"data\":}")]
        class CopyOptions { }

        [Verb("paste", HelpText = "Get content from clipboard")]
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
                    norm.data = HtmlFormatHelper.CreateHtmlFormat(org.data);
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
            Clipboard.ContentChanged += ClipboardContentChangedHandler;
        }

        async void ClipboardContentChangedHandler(object sender, object e)
        {
            await DoPaste(new PasteOptions { Format = "text", RPCFormat = true });
        }

        async Task<int> DoCopy(CopyOptions opts)
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

            DoCopyInternal(data);
            return 0;
        }

        void DoCopyInternal(IList<ClipboardData> data)
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
                    package.SetDataProvider(norm.cf, new DataProviderHandler(OnDeferredDataRequestHandler));
                }
            }

            Clipboard.SetContent(package);
            Clipboard.Flush();
        }

        async void OnDeferredDataRequestHandler(DataProviderRequest request)
        {
            // TODO: Empty data indicate that this data can be defer rendered
            var deferral = request.GetDeferral();
            try
            {
                m_dataNotifier = new TaskCompletionSource<ClipboardData>();
                Console.Write(ToRPCFormat(new { id = ++m_requestId, command = "get", args = request.FormatId }));

                // Get and put data in request
                var norm = NormalizeClipboardData(await m_dataNotifier.Task);
                request.SetData(norm.data);
            }
            finally
            {
                deferral.Complete();
            }
        }

        async Task<int> DoPaste(PasteOptions opts)
        {
            var data = Clipboard.GetContent();
            var format = ConvertToClipboardFormat(opts.Format);
            if (data.Contains(format))
            {
                var content = await data.GetDataAsync(format);
                if (opts.RPCFormat)
                {
                    Console.Write(ToRPCFormat(new { command = "paste", args = content}));
                }
                else
                {
                    Console.Write(content);
                }
            }

            return 0;
        }

        struct ClipboardCommand
        {
            public int id;
            public string command;
            public List<ClipboardData> data;
        }

        async Task<int> DoRunServer(ServerOptions opts)
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
                            case "copy":
                                {
                                    if (request.id == c_bypassRequestId)
                                    {
                                        DoCopyInternal(request.data);
                                    }
                                    else if (request.id == m_requestId)
                                    {
                                        // Delay rendering data request
                                        if (request.data != null)
                                        {
                                            m_dataNotifier?.TrySetResult(request.data[0]);
                                        }
                                    }
                                    break;
                                }
                            case "paste":
                                await DoPaste(new PasteOptions { Format = "Text" });
                                break;
                        }
                    }
                    catch (JsonException) { }
                }
            }
            catch (FormatException) { }

            return 0;
        }

        // Event to notify copy delegate about package ready
        private TaskCompletionSource<ClipboardData> m_dataNotifier = null;
        private int m_requestId = 0;
        private bool m_useBase64Encode = false;
        private const int c_bypassRequestId = -1;

        public int Run(string[] args)
        {
            return Parser.Default.ParseArguments<CopyOptions, PasteOptions, ServerOptions>(args)
                   .MapResult(
                        (CopyOptions opts) => DoCopy(opts).Result,
                        (PasteOptions opts) => DoPaste(opts).Result,
                        (ServerOptions opts) => DoRunServer(opts).Result,
                        errs => 0);
        }

        [STAThread]
        static void Main(string[] args)
        {
            var program = new Program();

            var inputThread = new Thread(() =>
            {
                try
                {
                    program.Run(args);
                }
                catch(Exception e)
                {
                    Console.Error.WriteLine(e.Message);
                }

                Application.Exit();
            });
            inputThread.SetApartmentState(ApartmentState.STA);
            inputThread.Start();

            // Message pump
            Application.Run();
        }

    }
}
