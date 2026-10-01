using System.Text.RegularExpressions;
namespace Photoreel;

public sealed class Reading
{
    public string Style { get; set; } = "clean"; public bool StyleMatched { get; set; }
    public double Pace { get; set; } = 1;
    public string? Order { get; set; }
    public string? Grade { get; set; }
    public bool? Music { get; set; }
    public double? Duration { get; set; }
    public List<string> Emphasis { get; set; } = []; public List<Dictionary<string, string>> Signals { get; set; } = []; public List<string> Unsupported { get; set; } = [];
    public void Note(string found, string meaning) => Signals.Add(new() { ["found"] = found, ["meaning"] = meaning });
}
public static class Concept
{
    static string[] Words(string text) => text.Split('|');
    static readonly Dictionary<string, string[]> StyleWords = new()
    {
        ["calm"] = Words("잔잔|감성|차분|힐링|편안|따뜻|포근|여유|조용|평화|고요|몽글|아련|서정|나른|calm|soft|gentle|peaceful|cozy|warm|relax"),
        ["upbeat"] = Words("신나|경쾌|활기|발랄|에너지|역동|빠른|빠르게|하이라이트|브이로그|vlog|흥겨|즐거|유쾌|힙|트렌디|릴스|쇼츠|신명|통통|upbeat|energetic|fun|fast|dynamic|highlight"),
        ["cinematic"] = Words("영화|시네마|웅장|다큐|드라마틱|장엄|진지|묵직|고급|세련|cinematic|epic|dramatic|movie"),
        ["nostalgic"] = Words("추억|레트로|필름|빈티지|옛날|아날로그|그리운|그리움|회상|향수|nostalg|retro|vintage|memory|memories"),
        ["clean"] = Words("깔끔|담백|심플|미니멀|기록|정리|단순|있는 그대로|clean|simple|minimal")
    };
    static readonly (string Label, string[] Words, string[] Keys)[] Emphasis = [
        ("인물",Words("인물|사람|얼굴|표정|people|portrait"),["person","people"]),
        ("풍경",Words("풍경|경치|자연|landscape|scenery"),["nature","sea","aerial","sunset"]),
        ("음식",Words("음식|먹방|맛집|먹은|food"),["food"]),
        ("동물",Words("반려|강아지|고양이|댕댕|냥이|pet|dog|cat"),["animal"])];
    static readonly (string[] Words, string What)[] Generation = [
        (Words("애니메이션|애니|만화|지브리|픽사|웹툰|일러스트|그림체|수채화|유화|캐릭터|anime|cartoon|pixar|ghibli"),"그림체를 바꾸는 스타일 변환"),
        (Words("움직이게|살아 움직|살아움직|움직이는 영상|걷는|파도가 치|바람에 흔들|립싱크|말하는|춤추"),"사진 속 장면에 실제 움직임을 만들어 넣는 생성"),
        (Words("내레이션|나레이션|목소리|자막|voiceover|narration"),"내레이션·자막 만들기")];
    static string? First(string text, string words) => First(text, Words(words));
    static string? First(string text, string[] words) => words.Where(w => text.Contains(w, StringComparison.Ordinal)).OrderBy(w => text.IndexOf(w, StringComparison.Ordinal)).ThenBy(w => w, StringComparer.Ordinal).FirstOrDefault();
    public static Reading Interpret(string? input)
    {
        var text = (input ?? "").ToLowerInvariant().Trim(); var r = new Reading();
        var matches = StyleWords.Select(kv => new { Style = kv.Key, Found = kv.Value.Where(w => text.Contains(w, StringComparison.Ordinal)).ToList() }).Where(x => x.Found.Count > 0).OrderByDescending(x => x.Found.Count).ThenBy(x => x.Found.Min(w => text.IndexOf(w, StringComparison.Ordinal))).ThenBy(x => x.Style, StringComparer.Ordinal).FirstOrDefault();
        if (matches is not null) { r.Style = matches.Style; r.StyleMatched = true; r.Note(string.Join(", ", matches.Found), "스타일 → " + Catalog.Styles[r.Style].Name); }
        string? word;
        if ((word = First(text, "음악 없이|음악없이|무음|음악 빼|음악은 빼|no music|without music")) is not null) { r.Music = false; r.Note(word, "음악 없이 만들기"); }
        if ((word = First(text, "흑백|모노톤|black and white|monochrome")) is not null) { r.Grade = "bw"; r.Note(word, "색감 → 흑백"); }
        if ((word = First(text, "시간순|시간 순|순서대로|찍은 순서|촬영 순|날짜순|날짜 순|chronolog")) is not null) { r.Order = "chronological"; r.Note(word, "순서 → 촬영 시각 순"); }
        else if ((word = First(text, "올린 순서|업로드 순|upload order")) is not null) { r.Order = "upload"; r.Note(word, "순서 → 올린 순서 그대로"); }
        if ((word = First(text, "느리게|느린|천천히|여유롭게|길게|slow")) is not null) { r.Pace = 1.35; r.Note(word, "호흡 → 장면을 더 길게"); }
        else if ((word = First(text, "빠르게|빠른|짧게|속도감|템포 빠|fast|quick")) is not null && r.Style != "upbeat") { r.Pace = .75; r.Note(word, "호흡 → 장면을 더 짧게"); }
        var m = Regex.Match(text, @"(\d+)\s*분(?:\s*(\d+)\s*초)?");
        if (m.Success) r.Duration = double.Parse(m.Groups[1].Value) * 60 + (m.Groups[2].Success ? double.Parse(m.Groups[2].Value) : 0);
        else { m = Regex.Match(text, @"(\d+)\s*(?:초|sec|seconds?)"); if (m.Success) r.Duration = double.Parse(m.Groups[1].Value); }
        if (r.Duration.HasValue) { r.Duration = Math.Clamp(r.Duration.Value, 5, 120); r.Note(m.Value, $"길이 → 약 {r.Duration:0}초"); }
        foreach (var (label, words, keys) in Emphasis) if ((word = First(text, words)) is not null) { r.Emphasis.AddRange(keys.Where(k => !r.Emphasis.Contains(k))); r.Note(word, "강조 → " + label + " 사진을 우선하고 조금 더 길게"); }
        foreach (var (words, what) in Generation) if ((word = First(text, words)) is not null) r.Unsupported.Add($"‘{word}’: {what}은(는) 생성 모델이 필요해 현재 엔진에서는 적용되지 않았습니다.");
        return r;
    }
}
