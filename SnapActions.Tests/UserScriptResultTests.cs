using System.Diagnostics;
using SnapActions.Actions.UserActions;
using Xunit;

namespace SnapActions.Tests;

/// <summary>
/// JS 脚本动作的返回值文本化：字符串直接使用，数组/普通对象转 JSON，数字/布尔走 String()。
/// 回归点：早先用 Jint 的 CLR ToString，<c>return result</c>（数组）会渲染成 "(5)[]" 这种调试形态，
/// 用户看到的「结果」既不是数据也不是报错。
/// 这里同时固定「宿主端永远拿到字符串或清晰的失败」这条边界。
/// </summary>
public class UserScriptResultTests
{
    private static string Run(string code)
    {
        var runner = new JsScriptRunner();
        Assert.True(runner.Run(code, "x", out var result, out var error), error);
        return result!;
    }

    private static string Wrap(string body) => "function JSAction(t) { " + body + " }";

    [Theory]
    [InlineData("return '';")]                                             // 空字符串：成功但结果为空
    [InlineData("return 42;")]
    [InlineData("return NaN;")]
    [InlineData("return Infinity;")]
    [InlineData("return -0;")]
    [InlineData("return 10n;")]
    [InlineData("return true;")]
    [InlineData("return Symbol('s');")]
    [InlineData("return function f(){};")]
    [InlineData("return new Date(2020, 0, 2);")]
    [InlineData("return /ab+c/;")]
    [InlineData("return new Map([['a',1]]);")]
    [InlineData("class P { constructor(){ this.x = 1; } } return new P();")]
    [InlineData("var o = Object.create(null); o.a = 1; return o;")]         // 连 toString 都没有
    [InlineData("var o = {}; o.self = o; return o;")]                       // 循环引用：JSON.stringify 会抛
    [InlineData("return new Uint8Array([1,2,3]);")]
    [InlineData("return Promise.resolve(1);")]                              // 异步不受支持，但也不能崩
    [InlineData("return { toString: function(){ throw new Error('nope'); } };")]
    public void AnyReturnValue_BecomesSomeText(string body)
    {
        var runner = new JsScriptRunner();
        Assert.True(runner.Run(Wrap(body), "x", out var result, out var error), error);
        Assert.NotNull(result);
    }

    [Fact]
    public void EmptyStringReturn_IsSuccessWithEmptyText()
    {
        Assert.Equal("", Run(Wrap("return '';")));
    }

    [Fact]
    public void TamperedConversionHelper_StillConvertsArrays()
    {
        // 宿主在建引擎时就把文本化函数抓在手里，所以脚本改写这个全局也影响不到结果转换。
        Assert.Equal("[1,2,3]", Run(Wrap("__snapResultText = null; return [1,2,3];")));
    }

    [Fact]
    public void MissingEntryPoint_GetsAFriendlyMessage()
    {
        var runner = new JsScriptRunner();
        Assert.False(runner.Run("var x = 1;", "x", out _, out var error));
        Assert.Contains("JSAction", error ?? "");
        Assert.DoesNotContain("Can only invoke functions", error ?? "");
    }

    [Fact]
    public void ArrayReturn_BecomesJson()
    {
        Assert.Equal("[1,2,3]", Run("function JSAction(t){ return [1,2,3]; }"));
    }

    [Fact]
    public void BuiltArrayOfFiveNumbers_DoesNotBecomeDebugText()
    {
        // 用户场景：int arr random(1,10)*5 这类脚本 return 一个 5 元素数组。
        var text = Run("function JSAction(t){ var r = []; for (var i = 1; i <= 5; i++) r.push(i); return r; }");
        Assert.Equal("[1,2,3,4,5]", text);
        Assert.DoesNotContain("(5)", text);
    }

    [Fact]
    public void ObjectReturn_BecomesJson()
    {
        Assert.Equal("{\"a\":1,\"b\":[2,3]}", Run("function JSAction(t){ return { a: 1, b: [2,3] }; }"));
    }

    [Fact]
    public void RandomIntArrayScript_ReturnsJsonNumbers()
    {
        // 用户实测脚本（int <名> random(min,max)*count 生成随机整数数组）的等价形态：
        // 期望 "[3,7,1,9,4]" 这样的 JSON，而不是 Jint 的 "(5)[]"。
        const string js = """
            function JSAction(text) {
              const m = /int\s+\w+\s+random\((\d+)\s*,\s*(\d+)\)\s*\*\s*(\d+)/.exec(text);
              if (!m) throw new Error('无法解析的格式: "' + text + '"');
              const min = parseInt(m[1], 10), max = parseInt(m[2], 10), count = parseInt(m[3], 10);
              if (min > max) throw new Error('最小值不能大于最大值');
              const result = [];
              for (let i = 0; i < count; i++) {
                result.push(Math.floor(Math.random() * (max - min + 1)) + min);
              }
              return result;
            }
            """;
        var runner = new JsScriptRunner();
        Assert.True(runner.Run(js, "int arr random(1,10)*5", out var result, out var error), error);
        Assert.Matches(@"^\[(\d{1,2})(,\d{1,2}){4}\]$", result);
    }

    [Fact]
    public void NumberAndBooleanAndString_KeepTheirTextForm()
    {
        Assert.Equal("42", Run("function JSAction(t){ return 42; }"));
        Assert.Equal("true", Run("function JSAction(t){ return true; }"));
        Assert.Equal("hello", Run("function JSAction(t){ return 'hello'; }"));
    }

    [Fact]
    public void UndefinedReturn_FailsWithClearMessage()
    {
        var runner = new JsScriptRunner();
        Assert.False(runner.Run("function JSAction(t){ }", "x", out _, out var error));
        Assert.Contains("undefined", error ?? "");
    }

    [Fact]
    public void CircularObject_FallsBackInsteadOfThrowing()
    {
        // JSON.stringify 对循环引用抛错：必须回退到 String()，不能让动作整段失败。
        var text = Run("function JSAction(t){ var o = {}; o.self = o; return o; }");
        Assert.False(string.IsNullOrEmpty(text));
        Assert.Contains("object", text);
    }
}
