using System.Globalization;
using System.Text;

namespace Studio;

public sealed record ScriptRequest(string Text, string Voice = "mimo_default", string Style = "",
    string Mode = "natural", string Character = "", string Scene = "", string Direction = "",
    string Tags = "", bool Singing = false, bool Split = true, int SegmentCharacters = 3000,
    string SynthesisMode = "preset", string VoiceDescription = "", string? SourceSampleId = null,
    bool OptimizeTextPreview = false);
public sealed record GenerationSummary(string SynthesisMode, string Model, string? Voice, string User,
    string Prefix, int Segments, string? SourceSampleId, string? SourceSampleName);
public sealed record ScriptPlan(string User, string Prefix, string[] Segments)
{
    public string[] Assistant => Segments.Select(s => Prefix + s).ToArray();
}
public static partial class Composition
{
    public static readonly string[] Voices = ["mimo_default", "冰糖", "茉莉", "苏打", "白桦", "Mia", "Chloe", "Milo", "Dean"];
    public static string Model(string mode) => mode switch
    {
        "preset" => "mimo-v2.5-tts", "design" => "mimo-v2.5-tts-voicedesign",
        "clone" => "mimo-v2.5-tts-voiceclone", _ => throw new InputError("无效的合成模式。")
    };
    public static ScriptPlan Plan(ScriptRequest r)
    {
        _ = Model(r.SynthesisMode);
        if (r.Text is null || r.Style is null || r.Character is null || r.Scene is null || r.Direction is null || r.Tags is null || r.VoiceDescription is null)
            throw new InputError("字段不能为 null。");
        if (r.OptimizeTextPreview) throw new InputError("本版不支持优化播报文本，请保留原文。");
        if (r.SynthesisMode != "preset" && (r.Singing || r.Voice != "mimo_default"))
            throw new InputError("VoiceDesign / VoiceClone 不支持预置音色或唱歌参数。");
        if (r.SynthesisMode == "design" && (string.IsNullOrWhiteSpace(r.VoiceDescription) || r.Tags.Length > 0))
            throw new InputError("VoiceDesign 需要音色描述，且不接受整体风格标签。");
        if (r.SynthesisMode != "design" && r.VoiceDescription.Length > 0)
            throw new InputError("音色描述仅适用于 VoiceDesign。");
        if (r.SynthesisMode == "clone" ? !Storage.ValidId(r.SourceSampleId ?? "") : r.SourceSampleId != null)
            throw new InputError("VoiceClone 必须提供合法样本 ID；其他模式不接受参考样本。");
        if (string.IsNullOrWhiteSpace(r.Text) || Encoding.UTF8.GetByteCount(r.Text) > 60000)
            throw new InputError("正文不能为空，且最多 60000 UTF-8 字节。");
        if (!Voices.Contains(r.Voice) || r.Mode is not ("natural" or "director" or "combined"))
            throw new InputError("音色或风格模式无效。");
        if (r.SegmentCharacters is < 50 or > 3000) throw new InputError("分段目标必须为 50–3000 个文本元素。");
        if (r.Tags.Length > 200 || r.Tags.IndexOfAny(['(', ')', '[', ']', '（', '）', '\r', '\n']) >= 0)
            throw new InputError("整体标签最多 200 字，直接填写名称，不包含括号或换行。");
        var director = string.Join("\n", new[] { ("角色", r.Character), ("场景", r.Scene), ("指导", r.Direction) }
            .Where(x => x.Item2.Length > 0).Select(x => x.Item1 + "：" + x.Item2));
        var user = r.Mode switch { "director" => director, "combined" => string.Join("\n", new[] { r.Style, director }.Where(x => x.Length > 0)), _ => r.Style };
        if (r.SynthesisMode == "design") user = string.Join("\n", new[] { r.VoiceDescription, user }.Where(x => x.Length > 0));
        if (Encoding.UTF8.GetByteCount(user) > 6000) throw new InputError("风格指令最多 6000 UTF-8 字节。");
        var prefix = (r.Singing ? "(唱歌)" : "") + (r.Tags.Length > 0 ? "(" + r.Tags + ")" : "");
        int budget = 3000 - Encoding.UTF8.GetByteCount(prefix);
        if (r.SynthesisMode == "design")
        {
            if (Encoding.UTF8.GetByteCount(r.Text) > budget)
                throw new InputError("VoiceDesign 仅支持单次试音（最多 3000 UTF-8 字节）。请先设计短样本，再用 VoiceClone 合成长文本；多次设计不保证同一音色。");
            return new(user, "", [r.Text]);
        }
        if (!r.Split)
        {
            if (Encoding.UTF8.GetByteCount(r.Text) > budget) throw new InputError("关闭分段时正文超过每段字节上限。");
            return new(user, prefix, [r.Text]);
        }
        // Treat complete bracket annotations and Unicode grapheme clusters as indivisible units.
        var units = new List<string>();
        var indices = StringInfo.ParseCombiningCharacters(r.Text);
        for (int i = 0; i < indices.Length; i++)
        {
            int start = indices[i], end = i + 1 < indices.Length ? indices[i + 1] : r.Text.Length;
            char close = r.Text[start] switch { '[' => ']', '(' => ')', '（' => '）', _ => '\0' };
            if (close != '\0')
            {
                int closing = r.Text.IndexOf(close, end);
                int newline = r.Text.IndexOfAny(['\r', '\n'], end);
                if (closing >= 0 && (newline < 0 || closing < newline))
                {
                    while (i + 1 < indices.Length && indices[i + 1] <= closing) i++;
                    end = i + 1 < indices.Length ? indices[i + 1] : r.Text.Length;
                }
            }
            units.Add(r.Text[start..end]);
        }
        var result = new List<string>();
        int pos = 0;
        while (pos < units.Count)
        {
            int bytes = 0, count = 0, end = pos, boundary = -1;
            while (end < units.Count)
            {
                int n = Encoding.UTF8.GetByteCount(units[end]);
                int chars = StringInfo.ParseCombiningCharacters(units[end]).Length;
                if (n > budget) throw new InputError("单个标签或文本元素超出分段字节上限。");
                if (bytes + n > budget || (count + chars > r.SegmentCharacters && end > pos)) break;
                bytes += n; count += chars; end++;
                if ("。！？!?；;\n\r. ".Contains(units[end - 1], StringComparison.Ordinal)) boundary = end;
            }
            if (end < units.Count && boundary > pos) end = boundary;
            if (end == pos) throw new InputError("无法安全分段。");
            result.Add(string.Concat(units.GetRange(pos, end - pos))); pos = end;
            if (result.Count > 80) throw new InputError("最多允许 80 段，请减少正文或提高分段目标。");
        }
        return new(user, prefix, result.ToArray());
    }
}
public class InputError(string message) : Exception(message);
public sealed class UpstreamError(int status, string message) : InputError(message) { public int Status { get; } = status; }
public sealed class EmptyAudioError() : InputError("服务返回空音频。");
