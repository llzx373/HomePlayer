namespace HomePlayer.AI;

public enum AiModelKind
{
    Recognition,
    Translation,
    VoiceActivityDetection,
    SenseVoiceComponent,
    SpeechSynthesis,
    SpeakerAnalysis
}

public sealed record AiModelInfo(
    string Id,
    AiModelKind Kind,
    string DisplayName,
    string Repository,
    string Revision,
    string FileName,
    long Size,
    string Sha256,
    string License,
    string? DownloadUrl = null,
    string? LocalName = null)
{
    public string LocalFileName => LocalName ?? FileName;
}

public sealed record AiLanguage(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public static class AiSubtitleCatalog
{
    public const string DefaultMirror = "https://hf-mirror.com";

    public static IReadOnlyList<AiModelInfo> Models { get; } =
    [
        new("whisper-small", AiModelKind.Recognition, "Whisper Small · 多语言平衡（466 MB）",
            "ggerganov/whisper.cpp", "5359861c739e955e79d9a303bcbc70fb988958b1", "ggml-small.bin", 487_601_967,
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", "MIT"),
        new("whisper-large-v3-turbo-q5", AiModelKind.Recognition, "Whisper Large V3 Turbo Q5 · 更高质量（548 MB）",
            "ggerganov/whisper.cpp", "5359861c739e955e79d9a303bcbc70fb988958b1", "ggml-large-v3-turbo-q5_0.bin", 574_041_195,
            "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", "MIT"),
        new("hy-mt2-7b-q4", AiModelKind.Translation, "Hy-MT2 7B Q4_K_M · 多语言本地翻译（4.3 GB）",
            "tencent/Hy-MT2-7B-GGUF", "ab8472660ac61fac25f1af43fac2599d52a8a775", "Hy-MT2-7B-Q4_K_M.gguf", 4_624_648_896,
            "9f96256500f3fc1ab4d64336b58f52a949a95ad7516b0c229476eef782f9f77b", "Apache-2.0"),
        new("hy-mt2-1.8b-q4", AiModelKind.Translation, "Hy-MT2 1.8B Q4_K_M · 轻量翻译（1.1 GB，适合 CPU/小显存）",
            "tencent/Hy-MT2-1.8B-GGUF", "a0c709d9fac510f2c807aa3af52872340dc37a4a", "Hy-MT2-1.8B-Q4_K_M.gguf", 1_133_080_448,
            "dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699", "Apache-2.0"),
        new("qwen3-4b-q4", AiModelKind.Translation, "Qwen3 4B Q4_K_M · 多语言本地翻译（2.33 GB）",
            "Qwen/Qwen3-4B-GGUF", "bc640142c66e1fdd12af0bd68f40445458f3869b", "Qwen3-4B-Q4_K_M.gguf", 2_497_280_256,
            "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5", "Apache-2.0"),
        new("silero-vad", AiModelKind.VoiceActivityDetection, "Silero VAD · 语音区间检测",
            "ggml-org/whisper-vad", "9ffd54a1e1ee413ddf265af9913beaf518d1639b", "ggml-silero-v6.2.0.bin", 885_098,
            "2aa269b785eeb53a82983a20501ddf7c1d9c48e33ab63a41391ac6c9f7fb6987", "MIT"),
        new("silero-vad-onnx", AiModelKind.VoiceActivityDetection, "Silero VAD ONNX · SenseVoice 语音区间检测",
            "k2-fsa/sherpa-onnx", "asr-models", "silero_vad.onnx", 643_854,
            "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6", "MIT",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx"),
        new("sensevoice-model", AiModelKind.SenseVoiceComponent, "SenseVoice Int8 模型",
            "csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17", "2365baeacb507f821a0c8120fcee3d484dba7a07", "model.int8.onnx", 239_233_841,
            "c71f0ce00bec95b07744e116345e33d8cbbe08cef896382cf907bf4b51a2cd51", "MIT"),
        new("sensevoice-tokens", AiModelKind.SenseVoiceComponent, "SenseVoice 词表",
            "csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17", "2365baeacb507f821a0c8120fcee3d484dba7a07", "tokens.txt", 315_894,
            "f449eb28dc567533d7fa59be34e2abca8784f771850c78a47fb731a31429a1dc", "MIT"),
        new("zipvoice-model", AiModelKind.SpeechSynthesis, "ZipVoice 语音克隆模型 · 中英（110 MB）",
            "k2-fsa/sherpa-onnx", "tts-models", "sherpa-onnx-zipvoice-distill-int8-zh-en-emilia.tar.bz2", 109_162_785,
            "77219c8b40f4ee8d73a7f902305ff6c1128ef9b54461c41b4ca6ed890b6c2803", "Apache-2.0",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/sherpa-onnx-zipvoice-distill-int8-zh-en-emilia.tar.bz2"),
        new("zipvoice-vocoder", AiModelKind.SpeechSynthesis, "Vocos 24 kHz 声码器（54 MB）",
            "k2-fsa/sherpa-onnx", "vocoder-models", "vocos_24khz.onnx", 54_157_409,
            "bcb3b970e384161c4d634f0bb9e999ff1c471b34c9bc0b1049a5014065ed3cc0", "MIT",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/vocoder-models/vocos_24khz.onnx"),
        new("diarization-segmentation", AiModelKind.SpeakerAnalysis, "说话人分割模型（7 MB）",
            "k2-fsa/sherpa-onnx", "speaker-segmentation-models", "sherpa-onnx-pyannote-segmentation-3-0.tar.bz2", 6_958_444,
            "24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488", "MIT",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2"),
        new("speaker-embedding", AiModelKind.SpeakerAnalysis, "声纹嵌入模型 Campplus（28 MB）",
            "k2-fsa/sherpa-onnx", "speaker-recongition-models", "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx", 28_281_138,
            "f682b514c05d947ee3fa91cd6ec6c5c7543479a128373fa29b1faedccd21fd11", "Apache-2.0",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx"),
        new("cosyvoice3-model", AiModelKind.SpeechSynthesis, "CosyVoice3 语音克隆模型（0.9 GB）",
            "Lourdle/Fun-CosyVoice3-0.5B-2512-GGUF", "main", "CosyVoice3-2512_Q8_0.gguf", 943_933_536,
            "be133cb6154ca73cd1d213b1c9496def99ba9c1f7c14cd99f6b350af2eb7963d", "Apache-2.0",
            LocalName: "CosyVoice3-2512_Q8_0.gguf"),
        new("cosyvoice-tokenizer", AiModelKind.SpeechSynthesis, "CosyVoice 语音分词器（0.9 GB）",
            "Lourdle/Fun-CosyVoice3-0.5B-2512-GGUF", "main", "frontend-onnx/speech_tokenizer_v3.onnx", 969_451_503,
            "23236a74175dbdda47afc66dbadd5bcb41303c467a57c261cb8539ad9db9208d", "Apache-2.0",
            LocalName: "speech_tokenizer_v3.onnx"),
        new("cosyvoice-campplus", AiModelKind.SpeechSynthesis, "Campplus 声纹前端（28 MB）",
            "Lourdle/Fun-CosyVoice3-0.5B-2512-GGUF", "main", "frontend-onnx/campplus.onnx", 28_303_423,
            "a6ac6a63997761ae2997373e2ee1c47040854b4b759ea41ec48e4e42df0f4d73", "Apache-2.0",
            LocalName: "campplus.onnx")
    ];

    public static IReadOnlyList<AiLanguage> Languages { get; } =
    [
        new("zh", "中文普通话"),
        new("en", "英语"),
        new("ja", "日语"),
        new("ko", "韩语"),
        new("pt", "葡萄牙语"),
        new("es", "西班牙语"),
        new("fr", "法语"),
        new("de", "德语"),
        new("ru", "俄语"),
        new("it", "意大利语")
    ];

    public static AiModelInfo Model(string id) =>
        Models.FirstOrDefault(model => model.Id == id)
        ?? throw new InvalidOperationException("未知或不受支持的模型：" + id);

    public static string LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "自动检测";
        var normalized = code.Split('-', '_')[0];
        return Languages.FirstOrDefault(language => language.Code == normalized)?.DisplayName ?? code;
    }
}
