namespace Photoreel;

public static class MusicSynth
{
    public const int SampleRate = 44100;
    const double Tau = 2 * Math.PI;
    static readonly (double, double)[] Sine = [(1, 1)], Soft = [(1, 1), (2, .25), (3, .08)], Pluck = [(1, 1), (2, .4), (3, .15), (4, .08)], Marimba = [(1, 1), (4, .25), (10, .04)], Epiano = [(1, 1), (2, .3), (3, .12), (5, .04)], Bass = [(1, 1), (2, .35), (3, .12)];
    static readonly (double, double)[] Strings = Enumerable.Range(1, 8).Select(k => ((double)k, 1 / Math.Pow(k, 1.4))).ToArray();
    static readonly Dictionary<string, (int Root, int[] Chord)[]> Progressions = new()
    {
        ["calm"] = [(48, [60, 64, 67, 71]), (45, [57, 60, 64, 67]), (41, [57, 60, 64, 65]), (43, [59, 62, 64, 67])],
        ["light"] = [(48, [60, 64, 67]), (45, [57, 60, 64]), (41, [57, 60, 65]), (43, [59, 62, 67])],
        ["upbeat"] = [(45, [57, 60, 64]), (41, [57, 60, 65]), (48, [60, 64, 67]), (43, [59, 62, 67])],
        ["cinematic"] = [(45, [57, 60, 64]), (41, [53, 57, 60]), (48, [55, 60, 64]), (43, [55, 59, 62])],
        ["lofi"] = [(50, [60, 62, 65, 69]), (43, [59, 62, 65, 69]), (48, [59, 60, 64, 67]), (45, [57, 60, 64, 67])]
    };
    static double Hz(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);
    static double Normal(Random rng) => Math.Sqrt(-2 * Math.Log(Math.Max(1e-15, rng.NextDouble()))) * Math.Cos(Tau * rng.NextDouble());
    static double[] Tone(int midi, double seconds, (double Mult, double Amp)[] partials, double attack = .005, double? decay = null, double release = .05, double detune = 0)
    {
        int n = Math.Max(2, (int)(seconds * SampleRate)), rel = Math.Min(n, (int)(release * SampleRate)); var output = new double[n]; double hz = Hz(midi);
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SampleRate, value = 0;
            foreach (var (mult, amp) in partials) if (hz * mult < SampleRate * .45) { value += amp * Math.Sin(Tau * hz * mult * t); if (detune != 0) value += amp * Math.Sin(Tau * hz * mult * (1 + detune) * t + 1.3); }
            double env = Math.Min(1, t / Math.Max(attack, 1e-4)); if (decay.HasValue) env *= Math.Exp(-t / decay.Value); if (rel > 1 && i >= n - rel) env *= (double)(n - 1 - i) / (rel - 1); output[i] = value * env;
        }
        return output;
    }
    static double[] Kick(double punch)
    {
        int n = (int)(.32 * SampleRate); var output = new double[n]; double phase = 0;
        for (int i = 0; i < n; i++) { double t = (double)i / SampleRate; phase += 46 + 90 * punch * Math.Exp(-t / .028); output[i] = Math.Sin(Tau * phase / SampleRate) * Math.Exp(-t / .11); }
        return output;
    }
    static double[] Noise(Random rng, double seconds, double tau, bool bright)
    {
        int n = (int)(seconds * SampleRate); var raw = Enumerable.Range(0, n + 8).Select(_ => Normal(rng)).ToArray(); var output = new double[n];
        for (int i = 0; i < n; i++)
        {
            double value;
            if (bright) value = (raw[i + 2] - 2 * raw[i + 1] + raw[i]) * .5;
            else { value = 0; for (int k = 0; k < 6; k++) { int at = i + k - 3; if (at >= 0 && at < raw.Length - 1) value += (raw[at + 1] - raw[at]) / 6; } value *= 1.6; }
            double t = (double)i / SampleRate; output[i] = value * Math.Exp(-t / tau) * Math.Min(1, t / .001);
        }
        return output;
    }
    sealed class Track(double seconds, int seed)
    {
        readonly int count = (int)(seconds * SampleRate) + SampleRate;
        readonly double[] dry = new double[((int)(seconds * SampleRate) + SampleRate) * 2], wet = new double[((int)(seconds * SampleRate) + SampleRate) * 2];
        public Random Rng { get; } = new(seed);
        public void Add(double[] signal, double at, double gain = 1, double pan = 0, double reverb = 0)
        {
            int start = (int)Math.Round(at * SampleRate); if (start < 0 || start >= count) return;
            double theta = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4, left = Math.Cos(theta) * gain, right = Math.Sin(theta) * gain;
            for (int i = 0; i < Math.Min(signal.Length, count - start); i++) { int p = (start + i) * 2; double l = signal[i] * left, r = signal[i] * right; dry[p] += l; dry[p + 1] += r; wet[p] += l * reverb; wet[p + 1] += r * reverb; }
        }
        public double[] Mix()
        {
            (double Delay, double Gain)[] taps = [(.023, .42), (.037, .36), (.053, .31), (.071, .27), (.097, .22), (.131, .18), (.167, .14), (.211, .11), (.263, .08), (.331, .06), (.419, .04)];
            for (int k = 0; k < taps.Length; k++) { int d = (int)(taps[k].Delay * SampleRate); for (int i = d; i < count; i++) for (int c = 0; c < 2; c++) dry[i * 2 + c] += wet[(i - d) * 2 + (k % 2 == 0 ? c : 1 - c)] * taps[k].Gain; }
            return dry;
        }
    }
    static double[] Build(string mood, double seconds, int bpm, int seed, CancellationToken token)
    {
        double beat = 60.0 / bpm; int bars = (int)Math.Ceiling(seconds / (4 * beat)) + 1; var progression = Progressions[mood]; var tr = new Track(bars * 4 * beat, seed); var rng = tr.Rng;
        for (int bar = 0; bar < bars; bar++)
        {
            token.ThrowIfCancellationRequested(); double t0 = bar * 4 * beat, build = Math.Min(1, .55 + .45 * bar / Math.Max(1, bars - 2)); var (root, chord) = progression[bar % 4];
            if (mood == "calm")
            {
                for (int i = 0; i < chord.Length; i++) tr.Add(Tone(chord[i], 4 * beat + .8, Soft, .5, null, 1, .003), t0, .075, (i - 1.5) * .3, .7);
                tr.Add(Tone(root, 4 * beat + .4, Sine, .2, null, .8), t0, .16);
                int[] pattern = [0, 2, 1, 3, 2, 1, 3, 2]; for (int k = 0; k < 8; k++) tr.Add(Tone(chord[pattern[k] % chord.Length] + 12, 1.4, Pluck, decay: .45, release: .2), t0 + k * beat / 2, k % 2 == 1 ? .07 : .10, k % 2 == 1 ? .35 : -.35, .8);
            }
            else if (mood == "light")
            {
                int[] pattern = [0, 1, 2, 1, 0, 2, 1, 2]; for (int k = 0; k < 8; k++) tr.Add(Tone(chord[pattern[k] % chord.Length] + 12, .7, Marimba, decay: .22, release: .1), t0 + k * beat / 2, k % 2 == 0 ? .16 : .11, k % 2 == 0 ? -.3 : .3, .45);
                for (int i = 0; i < chord.Length; i++) tr.Add(Tone(chord[i], 4 * beat + .3, Soft, .3, null, .6, .002), t0, .035, (i - 1) * .4, .5);
                foreach (int b in new[] { 0, 2 }) { tr.Add(Kick(.7), t0 + b * beat, .42); tr.Add(Tone(root - 12, beat * 1.6, Bass, .01, null, .15), t0 + b * beat, .22); }
                for (int k = 1; k < 8; k += 2) tr.Add(Noise(rng, .05, .014, true), t0 + k * beat / 2, .05, .2);
            }
            else if (mood == "upbeat")
            {
                for (int b = 0; b < 4; b++) { tr.Add(Kick(1), t0 + b * beat, .62); if (b is 1 or 3) tr.Add(Noise(rng, .16, .05, false), t0 + b * beat, .30, reverb: .25); }
                for (int k = 0; k < 8; k++) { tr.Add(Noise(rng, .045, .012, true), t0 + k * beat / 2, k % 2 == 1 ? .075 : .04, .25); tr.Add(Tone(root - 12 + (k % 2 == 0 ? 0 : 12), beat * .42, Bass, .004, null, .05), t0 + k * beat / 2, .25); }
                foreach (int k in new[] { 1, 3, 5, 7 }) for (int i = 0; i < chord.Length; i++) tr.Add(Tone(chord[i] + 12, .28, Pluck, decay: .12), t0 + k * beat / 2, .06, (i - 1) * .5, .3);
                int[] pattern = [0, 2, 1, 2, 0, 2, 1, 3]; for (int k = 0; k < 8; k++) tr.Add(Tone(chord[pattern[k] % chord.Length] + 24, .3, Marimba, decay: .12), t0 + k * beat / 2, .07, -.4, .5);
            }
            else if (mood == "cinematic")
            {
                for (int i = 0; i < chord.Length; i++) { tr.Add(Tone(chord[i], 4 * beat + 1, Strings, .9, null, 1.2, .004), t0, .045 * build, (i - 1) * .5, .8); tr.Add(Tone(chord[i] - 12, 4 * beat + 1, Soft, .9, null, 1.2, .003), t0, .04 * build, -(i - 1) * .4, .6); }
                tr.Add(Tone(root - 12, 4 * beat + .6, Bass, .3, null, .9), t0, .22); if (bar % 2 == 0) tr.Add(Kick(.5), t0, .6, reverb: .5);
                int[] pattern = [0, 2, 1, 2]; for (int k = 0; k < 4; k++) tr.Add(Tone(chord[pattern[k] % chord.Length] + 12, 1.6, Epiano, decay: .6, release: .3), t0 + k * beat, .10 * build, .2, .9);
            }
            else
            {
                foreach (double at in new[] { 0, 1.5 * beat }) for (int i = 0; i < chord.Length; i++) { var sig = Tone(chord[i], 1.5 * beat + .5, Epiano, decay: .9, release: .3); for (int n = 0; n < sig.Length; n++) sig[n] *= 1 + .12 * Math.Sin(Tau * 4.5 * n / SampleRate); tr.Add(sig, t0 + at, .075, (i - 1.5) * .3, .5); }
                tr.Add(Tone(root - 12, 2 * beat, Bass, .01, null, .2), t0, .26); tr.Add(Tone(root - 12, 1.2 * beat, Bass, .01, null, .2), t0 + 2.5 * beat, .2);
                foreach (double at in new[] { 0, 2.5 * beat }) tr.Add(Kick(.6), t0 + at, .5); foreach (int b in new[] { 1, 3 }) tr.Add(Noise(rng, .2, .06, false), t0 + b * beat, .2, reverb: .3);
                for (int k = 0; k < 8; k++) tr.Add(Noise(rng, .04, .010, true), t0 + k * beat / 2 + (k % 2 == 1 ? beat * .08 : 0), .035, -.2);
            }
        }
        var result = tr.Mix(); if (mood == "lofi") for (int i = 0; i < result.Length; i++) result[i] += Normal(rng) * .0012; return result;
    }
    public static object Synthesize(string path, double seconds, string mood, int bpm, CancellationToken token = default, int seed = 7)
    {
        var audio = Build(mood, seconds + .5, bpm, seed, token); int n = (int)Math.Round(seconds * SampleRate), fi = Math.Min(n, (int)(.25 * SampleRate)), fo = (int)(Math.Min(2, seconds / 4) * SampleRate); double power = 0;
        for (int i = 0; i < n; i++) { double gain = i < fi ? (double)i / Math.Max(1, fi - 1) : 1; if (i >= n - fo) gain *= Math.Pow((double)(n - 1 - i) / Math.Max(1, fo - 1), 1.5); for (int c = 0; c < 2; c++) { int at = i * 2 + c; audio[at] *= gain; power += audio[at] * audio[at]; } }
        double normalization = Math.Pow(10, -16.5 / 20) / (Math.Sqrt(power / (n * 2)) + 1e-9), peak = 0;
        for (int i = 0; i < n * 2; i++) { audio[i] = Math.Tanh(audio[i] * normalization * 1.2) / 1.2; peak = Math.Max(peak, Math.Abs(audio[i])); }
        double limit = peak > .89 ? .89 / peak : 1; power = 0;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + n * 4); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(SampleRate); writer.Write(SampleRate * 4); writer.Write((short)4); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(n * 4);
            for (int i = 0; i < n * 2; i++) { double value = audio[i] * limit; power += value * value; writer.Write((short)(Math.Clamp(value, -1, 1) * 32767)); }
        }
        return new { seconds = Math.Round((double)n / SampleRate, 3), bpm, mood, peak = Math.Round(peak * limit, 3), rms_db = Math.Round(20 * Math.Log10(Math.Sqrt(power / (n * 2)) + 1e-9), 1), source = "앱 내장 합성 (외부 음원 미사용)", noise = "C# seeded Random + Box-Muller; NumPy PCG64 파형과 다름" };
    }
}
