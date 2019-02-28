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
        }

        [Verb("server", HelpText = "Interactively get/put data to clipboard. Data format <size>\\r\\n{\"command\":<\"copy\"|\"paste\">, \"data\":[ClipboardData]}")]
        class ServerOptions { }

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

        async Task<int> DoCopy(CopyOptions opts)
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

            return DoCopyInternal(data);
        }

        int DoCopyInternal(IList<ClipboardData> data)
        {
            var package = new DataPackage();
            foreach (var d in data)
            {
                var norm = NormalizeClipboardData(d);
                package.SetData(norm.cf, norm.data);
            }

            Clipboard.SetContent(package);
            Clipboard.Flush();
            return 0;
        }

        async Task<int> DoPaste(PasteOptions opts)
        {
            var data = Clipboard.GetContent();
            var format = ConvertToClipboardFormat(opts.Format);
            if (data.Contains(format))
            {
                Console.Write(await data.GetDataAsync(format));
            }

            return 0;
        }

        struct ClipboardCommand
        {
            public string command;
            public List<ClipboardData> data;
        }

        async Task<int> DoRunServer(ServerOptions opts)
        {
            try
            {
                Int32 dataSize = 0;
                while ((dataSize = Convert.ToInt32(await Console.In.ReadLineAsync())) > 0)
                {
                    var buffer = new char[dataSize];
                    await Console.In.ReadBlockAsync(buffer, 0, dataSize);
                    try
                    {
                        var request = JsonConvert.DeserializeObject<ClipboardCommand>(new string(buffer));
                        switch (request.command)
                        {
                            case "copy":
                                DoCopyInternal(request.data);
                                break;
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

        public int Run(string[] args)
        {
            return Parser.Default.ParseArguments<CopyOptions, PasteOptions, ServerOptions>(args)
                   .MapResult(
                        (CopyOptions opts) => DoCopy(opts).Result,
                        (PasteOptions opts) => DoPaste(opts).Result,
                        (ServerOptions opts) => DoRunServer(opts).Result,
                        errs => 0);
        }

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
