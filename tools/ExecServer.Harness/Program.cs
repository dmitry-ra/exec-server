using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
var shutdown = false;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--include-shutdown") { shutdown = true; continue; }
    if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
        throw new ArgumentException("Expected --url URL --token KEY --fixture PATH [--dotnet PATH] [--include-shutdown]");
    options.Add(args[i], args[++i]);
}
if (options.Remove("--token-file", out string? keyFile))
{
    if (options.ContainsKey("--token")) throw new ArgumentException("Use either --token or --token-file.");
    options["--token"] = File.ReadAllText(keyFile).Trim();
}
foreach (var required in new[] { "--url", "--token", "--fixture" })
    if (!options.ContainsKey(required)) throw new ArgumentException($"Missing {required}");

using var client = new HttpClient();
client.BaseAddress = new Uri(options["--url"].TrimEnd('/') + "/");
client.Timeout = TimeSpan.FromSeconds(20);
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options["--token"]);
var suite = new Suite(client, options["--fixture"], options.GetValueOrDefault("--dotnet"));
await suite.RunAsync(shutdown);
return suite.Failed == 0 ? 0 : 1;

sealed class Suite(HttpClient client, string fixture, string? dotnet)
{
    private readonly List<object> _results = [];
    private readonly HashSet<string> _retained = [];
    public int Failed { get; private set; }

    public async Task RunAsync(bool shutdown)
    {
        await Case("authentication_rejects_before_child_start", AuthenticationAsync);
        await Case("invalid_requests_rejected_without_child_start", ValidationAsync);
        await Case("child_does_not_inherit_service_api_key", ChildEnvironmentAsync);
        await Case("separate_streams_and_exit_metadata", SeparateStreamsAsync);
        await Case("initial_and_later_stdin_with_eof", StdinAsync);
        await Case("initial_binary_stdin", BinaryStdinAsync);
        await Case("async_list_and_diagnostic_tree", ListingAsync);
        await Case("offset_and_repeat_reads", OffsetsAsync);
        await Case("invalid_utf8_retained_for_binary_retry", BinaryOutputAsync);
        await Case("full_large_output_and_blob_paging", LargeOutputAsync);
        await Case("durable_output_requires_explicit_delete", DurableAsync);
        await Case("default_tail_consumption_removes_execution", ConsumeAsync);
        await Case("tail_read_before_exit_releases_on_completion", EarlyTailConsumptionAsync);
        await Case("long_poll_wakes_on_new_output", LongPollAsync);
        await Case("completed_stdout_does_not_spin_stderr_long_poll", ClosedStreamLongPollAsync);
        await Case("signals_obey_platform", SignalsAsync);
        await CleanupAsync();
        if (shutdown) await Case("drain_rejects_launch_but_preserves_io", DrainAsync);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = _results.Count - Failed, failed = Failed, checks = _results }));
    }

    private async Task Case(string name, Func<Task> test)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            await test();
            _results.Add(new { name, status = "passed", milliseconds = elapsed.ElapsedMilliseconds });
        }
        catch (Exception error)
        {
            Failed++;
            // Do not print HTTP headers, credentials, or launch environment.
            _results.Add(new { name, status = "failed", milliseconds = elapsed.ElapsedMilliseconds, error = error.Message });
        }
    }

    private JsonObject Launch(string mode, string? argument = null, bool durable = true, object? input = null, bool close = false)
    {
        var childArgs = new JsonArray();
        if (dotnet is not null) childArgs.Add(fixture);
        childArgs.Add(mode);
        if (argument is not null) childArgs.Add(argument);
        var body = new JsonObject
        {
            ["executable"] = dotnet ?? fixture, ["args"] = childArgs,
            ["durable"] = durable, ["closeStdin"] = close, ["waitMilliseconds"] = 0
        };
        if (input is not null) body["stdin"] = JsonSerializer.SerializeToNode(input);
        return body;
    }

    private async Task<JsonNode> StartAsync(string mode, string? argument = null, bool durable = true, object? input = null, bool close = false)
    {
        using var response = await client.PostAsJsonAsync("v1/executions", Launch(mode, argument, durable, input, close));
        Check(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Accepted, $"launch returned {(int)response.StatusCode}");
        var record = await JsonAsync(response);
        _retained.Add(Text(record, "id"));
        return record;
    }

    private async Task<JsonNode> WaitAsync(string id, int milliseconds = 10000)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            using var response = await client.GetAsync($"v1/executions/{id}");
            Check(response.IsSuccessStatusCode, $"status returned {(int)response.StatusCode}");
            var record = await JsonAsync(response);
            if (Text(record, "state") == "exited" && record["captureComplete"]?.GetValue<bool>() == true) return record;
            await Task.Delay(20);
        }
        throw new InvalidOperationException("execution did not finish capture before bounded test deadline");
    }

    private async Task<JsonNode> OutputAsync(string id, string query = "")
    {
        using var response = await client.GetAsync($"v1/executions/{id}/output{query}");
        Check(response.IsSuccessStatusCode, $"output returned {(int)response.StatusCode}");
        return await JsonAsync(response);
    }

    private async Task<byte[]> ReadChunkAsync(JsonNode chunk, int limit = 4096)
    {
        if (chunk["text"] is { } text) return Encoding.UTF8.GetBytes(text.GetValue<string>());
        if (chunk["binary"] is { } binary) return Convert.FromBase64String(binary.GetValue<string>());
        if (chunk["blobId"] is not { } blob) return [];
        var offset = Number(chunk, "offset");
        var total = Number(chunk, "totalBytes");
        using var output = new MemoryStream();
        while (offset < total)
        {
            using var response = await client.GetAsync($"v1/blobs/{blob.GetValue<string>()}?offset={offset}&limit={limit}");
            Check(response.IsSuccessStatusCode, $"blob returned {(int)response.StatusCode}");
            Check(response.Content.Headers.ContentType?.MediaType == "application/octet-stream", "blob is not raw binary");
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Check(bytes.Length > 0 && bytes.Length <= limit, "blob ignored page limit or made no progress");
            await output.WriteAsync(bytes);
            offset += bytes.Length;
        }
        return output.ToArray();
    }

    private async Task AuthenticationAsync()
    {
        var marker = "exec-server-denied-" + Guid.NewGuid().ToString("N");
        string?[] keys = [null, "deliberately-invalid-harness-key"];
        foreach (var key in keys)
        {
            using var anonymous = new HttpClient();
            anonymous.BaseAddress = client.BaseAddress;
            anonymous.Timeout = TimeSpan.FromSeconds(5);
            if (key is not null) anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var status = await anonymous.GetAsync("v1/status");
            Check(status.StatusCode == HttpStatusCode.Unauthorized, "status exposed without a valid API key");
            using var launch = await anonymous.PostAsJsonAsync("v1/executions", Launch("marker", marker));
            Check(launch.StatusCode == HttpStatusCode.Unauthorized, "launch accepted without a valid API key");
            const string missing = "00000000-0000-0000-0000-000000000000";
            foreach (var path in new[] { "v1/openapi.json", "v1/executions", $"v1/executions/{missing}", $"v1/executions/{missing}/output", $"v1/executions/{missing}/processes", $"v1/blobs/{missing}" })
            {
                using var read = await anonymous.GetAsync(path);
                Check(read.StatusCode == HttpStatusCode.Unauthorized, "read endpoint accepted an invalid/missing key");
            }
            using var input = await anonymous.PostAsJsonAsync($"v1/executions/{missing}/stdin", new { text = "denied" });
            Check(input.StatusCode == HttpStatusCode.Unauthorized, "stdin endpoint accepted an invalid/missing key");
            using var eof = await anonymous.PostAsync($"v1/executions/{missing}/close-stdin", null);
            Check(eof.StatusCode == HttpStatusCode.Unauthorized, "EOF endpoint accepted an invalid/missing key");
            using var deletion = await anonymous.DeleteAsync($"v1/executions/{missing}");
            Check(deletion.StatusCode == HttpStatusCode.Unauthorized, "delete endpoint accepted an invalid/missing key");
            using var signal = await anonymous.PostAsJsonAsync("v1/signals", new { pid = int.MaxValue, signal = 15 });
            Check(signal.StatusCode == HttpStatusCode.Unauthorized, "signal endpoint accepted an invalid/missing key");
            using var stop = await anonymous.PostAsync("v1/shutdown", null);
            Check(stop.StatusCode == HttpStatusCode.Unauthorized, "shutdown endpoint accepted an invalid/missing key");
        }
        var probe = await StartAsync("probe-marker", marker);
        var id = Text(probe, "id");
        await WaitAsync(id);
        var data = await OutputAsync(id);
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(data["stdout"]!)) == "absent", "unauthorized request started the fixture");
    }

    private async Task ValidationAsync()
    {
        var marker = "exec-server-validation-" + Guid.NewGuid().ToString("N");
        var contradictory = Launch("marker", marker);
        contradictory["stdin"] = new JsonObject { ["text"] = "one", ["binary"] = "dHdv" };
        var invalidBinary = Launch("marker", marker);
        invalidBinary["stdin"] = new JsonObject { ["binary"] = "not!base64" };
        var unknown = Launch("marker", marker);
        unknown["unknownField"] = true;
        var emptyInput = Launch("marker", marker);
        emptyInput["stdin"] = new JsonObject();
        var nullText = Launch("marker", marker);
        nullText["stdin"] = new JsonObject { ["text"] = null };
        var nullBinary = Launch("marker", marker);
        nullBinary["stdin"] = new JsonObject { ["binary"] = null };
        var nullDurable = Launch("marker", marker);
        nullDurable["durable"] = null;
        foreach (var body in new[] { contradictory, invalidBinary, unknown, emptyInput, nullText, nullBinary, nullDurable })
        {
            using var response = await client.PostAsJsonAsync("v1/executions", body);
            Check(response.StatusCode == HttpStatusCode.BadRequest, "invalid launch body was accepted");
        }
        var id = Text(await StartAsync("probe-marker", marker), "id");
        await WaitAsync(id);
        var data = await OutputAsync(id);
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(data["stdout"]!)) == "absent", "invalid launch started a child");
    }

    private async Task ChildEnvironmentAsync()
    {
        var id = Text(await StartAsync("environment-key"), "id");
        await WaitAsync(id);
        var output = await OutputAsync(id);
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(output["stdout"]!)) == "absent", "child inherited the service API key");
    }

    private async Task SeparateStreamsAsync()
    {
        var id = Text(await StartAsync("emit"), "id");
        var state = await WaitAsync(id);
        Check(state["exitCode"]?.GetValue<int>() == 0, "wrong exit code");
        Check(Number(state, "pid") > 0 && Number(state, "stdoutBytes") == 4 && Number(state, "stderrBytes") == 4, "wrong metadata");
        var output = await OutputAsync(id);
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(output["stdout"]!)) == "OUT\n", "stdout changed or merged");
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(output["stderr"]!)) == "ERR\n", "stderr changed or merged");
    }

    private async Task StdinAsync()
    {
        var id = Text(await StartAsync("echo", input: new { text = "first:" }), "id");
        using var write = await client.PostAsJsonAsync($"v1/executions/{id}/stdin", new { text = "second" });
        Check(write.IsSuccessStatusCode, "later stdin failed");
        using var close = await client.PostAsync($"v1/executions/{id}/close-stdin", null);
        Check(close.IsSuccessStatusCode, "stdin EOF failed");
        await WaitAsync(id);
        var output = await OutputAsync(id);
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(output["stdout"]!)) == "first:second", "stdin ordering/content changed");
    }

    private async Task BinaryStdinAsync()
    {
        byte[] expected = [0, 255, 128, 10];
        var id = Text(await StartAsync("echo", input: new { binary = Convert.ToBase64String(expected) }, close: true), "id");
        await WaitAsync(id);
        var data = await OutputAsync(id, "?format=base64");
        Check((await ReadChunkAsync(data["stdout"]!)).SequenceEqual(expected), "binary stdin changed");
    }

    private async Task ListingAsync()
    {
        var id = Text(await StartAsync("sleep", "1000"), "id");
        using var listResponse = await client.GetAsync("v1/executions");
        Check(listResponse.IsSuccessStatusCode, "listing failed");
        var list = (await JsonAsync(listResponse)).AsArray();
        Check(list.Any(item => item?["id"]?.GetValue<string>() == id), "listing omitted managed execution");
        using var treeResponse = await client.GetAsync($"v1/executions/{id}/processes");
        Check(treeResponse.IsSuccessStatusCode, "diagnostic tree failed");
        var tree = (await JsonAsync(treeResponse)).AsArray();
        Check(tree.Count > 0 && tree.Any(item => item?["name"] is not null), "diagnostic tree omitted root process");
        await WaitAsync(id);
    }

    private async Task OffsetsAsync()
    {
        var id = Text(await StartAsync("emit"), "id");
        await WaitAsync(id);
        var first = (await OutputAsync(id, "?limit=2"))["stdout"]!;
        var repeated = (await OutputAsync(id, "?limit=2"))["stdout"]!;
        Check(Text(first, "text") == "OU" && Text(repeated, "text") == "OU", "omitted offset did not repeat from start");
        Check(Number(first, "nextOffset") == 2 && Number(first, "remainingBytes") == 2, "wrong paging metadata");
        var second = (await OutputAsync(id, "?stdoutOffset=2&stderrOffset=0&limit=2"))["stdout"]!;
        Check(Text(second, "text") == "T\n", "offset read changed bytes");
    }

    private async Task BinaryOutputAsync()
    {
        var id = Text(await StartAsync("binary"), "id");
        await WaitAsync(id);
        using var invalid = await client.GetAsync($"v1/executions/{id}/output");
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "invalid UTF8 silently accepted");
        var error = await JsonAsync(invalid);
        Check(error["code"] is not null && Number(error, "accumulatedBytes") == 5, "decode error missing type/accumulated raw length");
        var output = await OutputAsync(id, "?format=base64");
        Check((await ReadChunkAsync(output["stdout"]!)).SequenceEqual(new byte[] { 0, 255, 254, 128, 10 }), "failed text read consumed/corrupted binary");
    }

    private async Task LargeOutputAsync()
    {
        const int size = 2097152;
        var id = Text(await StartAsync("large-output", size.ToString(System.Globalization.CultureInfo.InvariantCulture)), "id");
        var state = await WaitAsync(id);
        Check(Number(state, "stdoutBytes") == size, "large output was truncated");
        var data = await OutputAsync(id, "?format=base64&limit=2097152");
        var chunk = data["stdout"]!;
        Check(chunk["blobId"] is not null, "large output was not represented as blob");
        var bytes = await ReadChunkAsync(chunk);
        Check(bytes.Length == size && bytes.All(value => value == (byte)'x'), "blob paging lost or changed bytes");
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(data["stderr"]!)) == "large-error\n", "large stderr was merged/lost");
    }

    private async Task DurableAsync()
    {
        var id = Text(await StartAsync("emit"), "id");
        await WaitAsync(id);
        await OutputAsync(id);
        using var status = await client.GetAsync($"v1/executions/{id}");
        Check(status.IsSuccessStatusCode, "durable read deleted metadata");
        using var deleted = await client.DeleteAsync($"v1/executions/{id}");
        Check(deleted.StatusCode == HttpStatusCode.NoContent, "durable delete failed");
        using var missing = await client.GetAsync($"v1/executions/{id}");
        Check(missing.StatusCode == HttpStatusCode.NotFound, "explicit delete retained metadata");
        _retained.Remove(id);
    }

    private async Task ConsumeAsync()
    {
        var id = Text(await StartAsync("emit", durable: false), "id");
        await WaitAsync(id);
        await OutputAsync(id);
        for (var i = 0; i < 30; i++)
        {
            using var status = await client.GetAsync($"v1/executions/{id}");
            if (status.StatusCode == HttpStatusCode.NotFound) { _retained.Remove(id); return; }
            await Task.Delay(20);
        }
        throw new InvalidOperationException("non-durable tail read did not remove execution");
    }

    private async Task LongPollAsync()
    {
        var id = Text(await StartAsync("delayed-output", "1200"), "id");
        for (var i = 0; ; i++)
        {
            using var status = await client.GetAsync($"v1/executions/{id}");
            var record = await JsonAsync(status);
            if (Number(record, "stdoutBytes") >= 6) break;
            Check(i < 100, "initial output did not arrive");
            await Task.Delay(10);
        }
        var watch = Stopwatch.StartNew();
        var data = await OutputAsync(id, "?stdoutOffset=6&stderrOffset=0&waitMilliseconds=5000");
        Check(watch.ElapsedMilliseconds < 4500, "longpoll did not wake on new output");
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(data["stdout"]!)) == "second\n", "longpoll returned before new output");
        await WaitAsync(id);
    }

    private async Task EarlyTailConsumptionAsync()
    {
        var id = Text(await StartAsync("tail-before-exit", durable: false), "id");
        JsonNode data;
        for (var i = 0; ; i++)
        {
            data = await OutputAsync(id);
            if (Number(data["stdout"]!, "totalBytes") == 5) break;
            Check(i < 100, "fixture did not emit its initial tail");
            await Task.Delay(10);
        }
        Check(Text(data["execution"]!, "state") == "running", "fixture exited before the causal tail read");
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(data["stdout"]!)) == "tail\n", "tail bytes changed");
        using var close = await client.PostAsync($"v1/executions/{id}/close-stdin", null);
        Check(close.IsSuccessStatusCode, "failed to let the tail fixture exit");
        for (var i = 0; i < 250; i++)
        {
            // Do not read output again: completion itself must release consumed data.
            using var status = await client.GetAsync($"v1/executions/{id}");
            if (status.StatusCode == HttpStatusCode.NotFound) { _retained.Remove(id); return; }
            Check(status.IsSuccessStatusCode, "status failed while awaiting release");
            await Task.Delay(20);
        }
        throw new InvalidOperationException("completion retained already-consumed non-durable output");
    }

    private async Task ClosedStreamLongPollAsync()
    {
        var id = Text(await StartAsync("closed-stdout-delayed-stderr", "800"), "id");
        for (var i = 0; ; i++)
        {
            var initial = await OutputAsync(id);
            if (initial["stdout"]?["eof"]?.GetValue<bool>() == true
                && Number(initial["stderr"]!, "totalBytes") == 6) break;
            Check(i < 100, "fixture stdout did not close before the long-poll test");
            await Task.Delay(10);
        }
        using var close = await client.PostAsync($"v1/executions/{id}/close-stdin", null);
        Check(close.IsSuccessStatusCode, "could not begin delayed stderr write");
        var clock = Stopwatch.StartNew();
        var output = await OutputAsync(id, "?stdoutOffset=6&stderrOffset=6&waitMilliseconds=5000");
        Check(clock.ElapsedMilliseconds >= 200 && clock.ElapsedMilliseconds < 4500, "completed stdout short-circuited stderr long-poll");
        Check(Encoding.UTF8.GetString(await ReadChunkAsync(output["stderr"]!)) == "later\n", "long-poll returned without delayed stderr");
        await WaitAsync(id);
    }

    private async Task SignalsAsync()
    {
        using var status = await client.GetAsync("v1/status");
        var os = Text(await JsonAsync(status), "operatingSystem");
        var record = await StartAsync("sleep", "3000");
        var id = Text(record, "id");
        using var signal = await client.PostAsJsonAsync("v1/signals", new { pid = Number(record, "pid"), signal = 15 });
        if (os.Contains("windows", StringComparison.OrdinalIgnoreCase))
            Check(signal.StatusCode == HttpStatusCode.NotImplemented, "Windows emulated a Unix signal");
        else
            Check(signal.StatusCode == HttpStatusCode.NoContent, "Unix signal failed");
        await WaitAsync(id);
    }

    private async Task DrainAsync()
    {
        var id = Text(await StartAsync("echo"), "id");
        try
        {
            using var draining = await client.PostAsync("v1/shutdown", null);
            Check(draining.StatusCode == HttpStatusCode.Accepted, "shutdown did not enter draining");
            using var status = await client.GetAsync("v1/status");
            Check(Text(await JsonAsync(status), "mode") == "draining", "status omitted draining mode");
            using var refused = await client.PostAsJsonAsync("v1/executions", Launch("emit"));
            Check(refused.StatusCode == HttpStatusCode.Conflict, "draining accepted a new launch");
            using var write = await client.PostAsJsonAsync($"v1/executions/{id}/stdin", new { text = "drain", close = true });
            Check(write.IsSuccessStatusCode, "draining rejected existing-process I/O");
            // Service may exit immediately after this last root ends: do not require
            // a post-exit response or turn a deliberate shutdown into a flaky check.
            _retained.Remove(id);
        }
        finally
        {
            if (_retained.Contains(id))
            {
                try
                {
                    using var close = await client.PostAsync($"v1/executions/{id}/close-stdin", null);
                    if (close.IsSuccessStatusCode || close.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
                        _retained.Remove(id);
                }
                catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
                {
                    // Preserve the original assertion/transport failure; root can
                    // reconcile the known execution if the service is unreachable.
                }
            }
        }
    }

    private async Task CleanupAsync()
    {
        foreach (var id in _retained.ToArray())
        {
            try
            {
                // These are exclusively this harness's retained executions.
                // A failed stdin/long-poll assertion must not leave a fixture
                // waiting forever for its client to close the input pipe.
                using var close = await client.PostAsync($"v1/executions/{id}/close-stdin", null);
                if (close.StatusCode == HttpStatusCode.NotFound)
                {
                    _retained.Remove(id);
                    continue;
                }
                Check(close.IsSuccessStatusCode || close.StatusCode == HttpStatusCode.Conflict,
                    "test execution stdin cleanup failed");
                await WaitAsync(id);
                using var response = await client.DeleteAsync($"v1/executions/{id}");
                Check(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound, "test execution cleanup failed");
                _retained.Remove(id);
            }
            catch (Exception error)
            {
                Failed++;
                _results.Add(new { name = "cleanup", status = "failed", error = error.Message });
            }
        }
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("missing JSON response");
    private static string Text(JsonNode node, string key) => node[key]?.GetValue<string>() ?? throw new InvalidOperationException($"missing {key}");
    private static long Number(JsonNode node, string key) => node[key]?.GetValue<long>() ?? throw new InvalidOperationException($"missing {key}");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
