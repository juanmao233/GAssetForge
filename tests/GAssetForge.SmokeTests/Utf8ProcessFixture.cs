using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GAssetForge.SmokeTests;

internal static class Utf8ProcessFixture
{
    internal const string Argument = "--utf8-echo-fixture";

    internal static int Run()
    {
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.Error.WriteLine("子进程错误流中文");
        var input = Console.ReadLine();
        Console.WriteLine(JsonSerializer.Serialize(
            new { method = "fixture/status", @params = new { text = "子进程回显：" + input } },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }
}
