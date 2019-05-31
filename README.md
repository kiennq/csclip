# csclip
Clipboard tool that monitor clipboard and support delay-rendering for any text editor in `Windows`

## Usage
`csclip` can be run as one time executable or as server process that communicating via stdio.
### Running as one time executable
Usage:
1. Help

``` shell
csclip help
```

2. Paste text (default) or any format from clipboard

``` shell
csclip paste
csclip paste -f <text|html|any other clipboard format id>
```

3. Copy to clipboard
Just need to pipe input to `csclip copy`.
``` shell
echo 'Test' | csclip copy
```

Or even better, you can using json and put multiple data format (or just one) to clipboard.

``` shell
echo '[{"cf":"text", "data":"test"}, {"cf":"html", "data":"<b>text in bold</b>"}]' | csclip copy
```

This will put `test` as text format to clipboard and put `<b>text in bold</b>` as html format to clipboard at the same time.
To know more about clipboard refer to [this article](https://docs.microsoft.com/en-us/windows/desktop/dataxchg/clipboard-formats)

### Running as server process
In this mode, `csclip` will communicate with client process (normally text editor) via stdio.
Client process should watch `csclip` stdout for notification.
Both side will communicating via [`JsonRPC`](https://www.jsonrpc.org/)

``` shell
csclip server
```

#### Monitoring clipboard change
When running as server, `csclip` will monitoring clipboard change and notify client about newly copied text.
With that, newly copied text will always be available in client, and can be paste without querying the clipboard again.

Notification `paste`:

``` json
{"method":"paste", "params":["<copied string>"]}
```

### Proactively getting text from clipboard
Client can proactively getting text from clipboard by sending `get` request.

``` json
{"method":"get", "params":["<data format>"]}
```

`csclip` will response with 

``` json
{"result":"<requested data>"}
```

### Copy data to clipboard
Client can sent `copy` notification to `csclip` to notify the data should be copied to clipboard.

``` json
{"method":"copy", "params":[[{"cf": "<data format>", "data": "<data to put into clipboard>"},{}]]}
```

### Delay copying to clipboard
Sometimes, it's not practically (and performant-wise) to put all of clipboard format data to clipboard directly when copying.
This happens a lot when user copy some texts that can be hightlight using html (image) format (which will require client to convert the text to that format).
In that case, client can put those data formats to clipboard latter by leave the `data` field for those formats to `null`

``` json
{"method":"copy", "params":[[{"cf": "<data format>", "data":null},{}]]}
```

When other application request those data formats, `csclip` will send `get` request to client.

``` json
{"method":"get", "params":["<data format>"]}
```

Upon receving that, client should render the required format and response back the result to `csclip`

``` json
{"result":"<requested data>"}
```

Please refer to [multiclip-mode](https://github.com/kiennq/highlight2clipboard) for example of client implementation.
