namespace Photoreel;

public record Style(string Id, string Name, string Description, int Bpm, int Beats, int MinBeats, int MaxBeats, string Transition, double TransitionSec, string ChapterTransition, double ZoomRate, double PanRate, string Grade, double Grain, double Vignette, string MusicMood)
{
    public double BeatSec => 60.0 / Bpm;
    public object Public() => new { Id, Name, Description, Bpm, Beats, MinBeats, MaxBeats, Transition, TransitionSec, ChapterTransition, ZoomRate, PanRate, Grade, Grain, Vignette, MusicMood, summary = $"장면당 약 {Beats * BeatSec:F1}초 · {Catalog.Transitions[Transition]} · {Catalog.Grades[Grade]} · {Catalog.Moods[MusicMood]} 음악({Bpm}BPM)" };
}
public static class Catalog
{
    public static readonly Dictionary<string, int[]> Aspects = new() { ["9:16"] = [1080, 1920], ["4:5"] = [1080, 1350], ["1:1"] = [1080, 1080], ["16:9"] = [1920, 1080] };
    public static readonly Dictionary<string, string> Motions = new() { ["zoom-in"] = "천천히 다가가기", ["zoom-out"] = "천천히 멀어지기", ["pan-right"] = "오른쪽으로 훑기", ["pan-left"] = "왼쪽으로 훑기", ["pan-up"] = "위로 훑기", ["pan-down"] = "아래로 훑기", ["hold"] = "고정" };
    public static readonly Dictionary<string, string> Transitions = new() { ["cut"] = "바로 전환", ["crossfade"] = "부드럽게 겹치기", ["fade-black"] = "어두워졌다 밝아지기", ["push-left"] = "옆으로 밀기", ["push-up"] = "위로 밀기" };
    public static readonly Dictionary<string, string> Framings = new() { ["cover"] = "화면 가득 채우기 (일부 잘림)", ["fit-blur"] = "넓게 보이기 (남는 곳은 흐린 배경)" };
    public static readonly Dictionary<string, string> Grades = new() { ["neutral"] = "원본 색 그대로", ["warm"] = "따뜻한 색감", ["vivid"] = "선명한 색감", ["cine"] = "차분한 영화 색감", ["film"] = "바랜 필름 색감", ["bw"] = "흑백" };
    public static readonly Dictionary<string, string> Moods = new() { ["calm"] = "잔잔한", ["upbeat"] = "경쾌한", ["cinematic"] = "웅장한", ["lofi"] = "나른한", ["light"] = "가벼운" };
    public static readonly Dictionary<string, Style> Styles = new Style[]{
        new("clean","담백한 기록","과한 효과 없이 사진을 또렷하게 보여 준다.",100,4,3,6,"crossfade",.4,"crossfade",.030,.055,"neutral",0,0,"light"),
        new("calm","잔잔한 감성","느린 호흡과 부드러운 전환, 따뜻한 색감.",76,4,3,6,"crossfade",.9,"crossfade",.022,.040,"warm",0,.12,"calm"),
        new("upbeat","경쾌한 하이라이트","박자에 맞춘 빠른 컷과 큰 움직임, 선명한 색감.",116,4,2,6,"cut",0,"push-left",.055,.085,"vivid",0,0,"upbeat"),
        new("cinematic","영화 같은 분위기","느리게 다가가는 움직임, 어둡게 넘어가는 전환, 차분한 색감.",84,4,3,6,"crossfade",.7,"fade-black",.028,.045,"cine",.015,.22,"cinematic"),
        new("nostalgic","필름 같은 추억","바랜 색과 필름 입자, 여유 있는 호흡.",88,4,3,6,"crossfade",.6,"fade-black",.025,.045,"film",.035,.18,"lofi")
    }.ToDictionary(s => s.Id);
}
public sealed class Scene
{
    public string PhotoId { get; set; } = ""; public bool Included { get; set; } = true; public int Beats { get; set; } = 4;
    public double Duration { get; set; }
    public double Start { get; set; }
    public string Motion { get; set; } = "zoom-in"; public string Framing { get; set; } = "cover"; public string Transition { get; set; } = "crossfade";
    public int Chapter { get; set; }
    public string Reason { get; set; } = ""; public string FramingReason { get; set; } = ""; public string ExcludeReason { get; set; } = "";
    public void Check()
    {
        if (PhotoId is null || Motion is null || Framing is null || Transition is null) throw new ArgumentException("장면 설정에 null을 사용할 수 없습니다.");
        if (Beats is < 1 or > 16) throw new ArgumentException("박자 수는 1~16이어야 합니다.");
        if (!Catalog.Motions.ContainsKey(Motion)) throw new ArgumentException("알 수 없는 움직임: " + Motion);
        if (!Catalog.Framings.ContainsKey(Framing)) throw new ArgumentException("알 수 없는 화면 맞춤: " + Framing);
        if (!Catalog.Transitions.ContainsKey(Transition)) throw new ArgumentException("알 수 없는 전환: " + Transition);
    }
}
public sealed class Output { public string Aspect { get; set; } = "9:16"; public int Width { get; set; } = 1080; public int Height { get; set; } = 1920; public int Fps { get; set; } = 30; }
public sealed class Music { public string Mode { get; set; } = "synth"; public string Mood { get; set; } = "light"; public int Bpm { get; set; } = 100; }
public sealed class Storyboard
{
    public int Version { get; set; } = 1; public string Concept { get; set; } = ""; public string Title { get; set; } = "";
    public string Style { get; set; } = "clean"; public string StyleReason { get; set; } = ""; public string Grade { get; set; } = "neutral";
    public Output Output { get; set; } = new(); public Music Music { get; set; } = new(); public string Engine { get; set; } = "edit";
    public string Planner { get; set; } = "local-rules"; public string OrderMode { get; set; } = "content"; public string OrderBasis { get; set; } = "";
    public List<Dictionary<string, string>> Signals { get; set; } = []; public List<string> Unsupported { get; set; } = []; public List<string> Notes { get; set; } = [];
    public List<Scene> Scenes { get; set; } = []; public double TotalDuration { get; set; }
    public double? TargetDuration { get; set; }
    public IEnumerable<Scene> Included() => Scenes.Where(s => s.Included);
    public Storyboard FinalizeBoard()
    {
        if (Style is null || Grade is null || Engine is null || Output is null || Music is null || Scenes is null || Scenes.Any(s => s is null) || Output.Aspect is null || Music.Mood is null || Title is null || Concept is null)
            throw new ArgumentException("구성안의 필수 설정에 null을 사용할 수 없습니다.");
        if (!Catalog.Styles.ContainsKey(Style)) throw new ArgumentException("알 수 없는 스타일: " + Style);
        if (!Catalog.Grades.ContainsKey(Grade)) throw new ArgumentException("알 수 없는 색감: " + Grade);
        if (!Catalog.Moods.ContainsKey(Music.Mood)) throw new ArgumentException("알 수 없는 음악 분위기: " + Music.Mood);
        if (Music.Mode is not "synth" and not "none" || Music.Bpm is < 50 or > 180) throw new ArgumentException("잘못된 음악 설정");
        if (Catalog.Aspects.TryGetValue(Output.Aspect, out var size)) { Output.Width = size[0]; Output.Height = size[1]; }
        if (Output.Width is < 240 or > 3840 || Output.Height is < 240 or > 3840 || Output.Fps is < 12 or > 60) throw new ArgumentException("출력 크기/프레임 수가 범위를 벗어남");
        if (Scenes.Count > 80) throw new ArgumentException("사진은 최대 80장까지입니다.");
        Scenes = Included().Concat(Scenes.Where(s => !s.Included)).ToList(); double t = 0, beat = 60.0 / Music.Bpm;
        foreach (var scene in Scenes) { scene.Check(); scene.Duration = scene.Included ? Math.Round(scene.Beats * beat, 4) : 0; scene.Start = scene.Included ? Math.Round(t, 4) : 0; if (scene.Included) t += scene.Duration; }
        TotalDuration = Math.Round(t, 3); return this;
    }
}
