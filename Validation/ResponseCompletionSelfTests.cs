using System.IO.Compression;
using System.Text;
using CodexAccountSwitcher.Core;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

public static class ResponseCompletionSelfTests
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var sse = Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"synthetic\"}\n\ndata:{\"type\":\"response.completed\"}\n\n");
        foreach (var encoding in new[] {"identity","gzip","br","deflate"})
        {
            var wire = sse;
            if (encoding != "identity")
            {
                using var output = new MemoryStream();
                using (Stream encoder = encoding switch
                {
                    "gzip" => new GZipStream(output,CompressionLevel.Fastest,true),
                    "br" => new BrotliStream(output,CompressionLevel.Fastest,true),
                    _ => new DeflateStream(output,CompressionLevel.Fastest,true)
                }) encoder.Write(sse);
                wire = output.ToArray();
            }
            await using var observer = new ResponseCompletionObserver("text/event-stream",[encoding]);
            for (var offset=0;offset<wire.Length;offset+=3)
                await observer.ObserveDeliveredAsync(wire.AsMemory(offset,Math.Min(3,wire.Length-offset))).ConfigureAwait(false);
            await observer.FinishAsync().ConfigureAwait(false);
            Require(observer.Completed && observer.Encoding == encoding, "分块或编码的成功终止事件未识别");
        }
        await using var incomplete = new ResponseCompletionObserver("text/event-stream",[]);
        await incomplete.ObserveDeliveredAsync(Encoding.UTF8.GetBytes("data:{\"type\":\"response.completed\"}\n\ndata:{\"type\":\"response.incomplete\"}\n\n")).ConfigureAwait(false);
        await incomplete.FinishAsync().ConfigureAwait(false); Require(!incomplete.Completed,"不完整事件不得报告完成");
        await using var unknown = new ResponseCompletionObserver("text/event-stream",["zstd"]);
        await unknown.ObserveDeliveredAsync(sse).ConfigureAwait(false); await unknown.FinishAsync().ConfigureAwait(false); Require(!unknown.Completed,"不支持的编码不得猜测成功");
        await using var json = new ResponseCompletionObserver("application/json",[]);
        await json.ObserveDeliveredAsync(Encoding.UTF8.GetBytes("{\"object\":\"response\",\"status\":\"completed\"}")).ConfigureAwait(false);
        await json.FinishAsync().ConfigureAwait(false); Require(json.Completed,"完整 JSON 响应状态未识别");
        await using var compact = new ResponseCompletionObserver("application/json",[]);
        await compact.ObserveDeliveredAsync(Encoding.UTF8.GetBytes("{\"output\":[],\"status\":\"completed\"}")).ConfigureAwait(false);
        await compact.FinishAsync().ConfigureAwait(false); Require(!compact.Completed,"辅助 JSON 不得冒充模型完成");
        await using var unspecified=new ResponseCompletionObserver(null,[]);
        await unspecified.ObserveDeliveredAsync(sse).ConfigureAwait(false);await unspecified.FinishAsync().ConfigureAwait(false);
        Require(unspecified.Completed,"缺失 Content-Type 的合法终止事件应可观察");
        return ["PASS: SSE 原始、identity、gzip、Brotli、deflate 分块终止事件均识别；失败及未知编码不报告完成。",
            "PASS: 仅完整 Responses JSON 可证明完成，辅助 JSON 与不完整响应不能伪造完成。"];
    }
    private static void Require(bool condition,string message)
    { if(!condition)throw new UserFacingException("隔离完成记录验证失败："+message+"。"); }
}
