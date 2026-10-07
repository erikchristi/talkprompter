using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Teleprompter.Core.Speech;
using Vosk;

namespace Teleprompter.Speech;

/// <summary>
/// Offline streaming recognition using Vosk (Kaldi). Emits partial hypotheses
/// as the reader speaks and a final when Vosk detects an utterance boundary.
///
/// When a <paramref name="vocabulary"/> is supplied (the words of the loaded
/// script), the recognizer runs in <b>grammar mode</b>: it is constrained to
/// only those words. Because the script is known ahead of time, this turns the
/// hard problem "recognize any English word" into the easy one "pick from these
/// known words", which is far more accurate for fast or accented speech and
/// decodes faster. Off-script speech falls through to Vosk's "[unk]" token,
/// which is stripped out so it simply produces no match (a correct pause).
/// </summary>
public sealed class VoskSpeechEngine : ISpeechEngine
{
    private const int MaxGrammarWords = 1200;

    // Vosk's JSON reader does not decode \u escapes, so every non-ASCII word
    // (größe, łódź, привет) must be written literally or it silently drops out
    // of the grammar. Words are letters and digits only, so relaxed escaping
    // is safe here.
    private static readonly JsonSerializerOptions GrammarJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Model _model;
    private readonly VoskRecognizer _recognizer;
    private string _lastPartial = string.Empty;

    public int SampleRate { get; } = 16000;

    public bool UsingGrammar { get; }

    public event EventHandler<SpeechHypothesis>? HypothesisReceived;

    public VoskSpeechEngine(string modelPath, IReadOnlyList<string>? vocabulary = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("A Vosk model path is required.", nameof(modelPath));
        }

        global::Vosk.Vosk.SetLogLevel(-1);
        _model = new Model(modelPath);

        string? grammar = BuildGrammar(vocabulary);
        if (grammar is not null)
        {
            try
            {
                _recognizer = new VoskRecognizer(_model, SampleRate, grammar);
                UsingGrammar = true;
            }
            catch (Exception)
            {
                // Some words may be out of the model's lexicon; fall back to
                // open-vocabulary recognition rather than failing to start.
                _recognizer = new VoskRecognizer(_model, SampleRate);
                UsingGrammar = false;
            }
        }
        else
        {
            _recognizer = new VoskRecognizer(_model, SampleRate);
            UsingGrammar = false;
        }
    }

    public void Start()
    {
        // The recognizer is ready on construction; nothing to warm up.
    }

    public void Stop()
    {
        try
        {
            Emit(_recognizer.FinalResult(), isFinal: true);
        }
        catch (Exception)
        {
            // Ignore flush errors on shutdown.
        }
    }

    public void AcceptWaveform(byte[] pcm16, int count)
    {
        try
        {
            if (_recognizer.AcceptWaveform(pcm16, count))
            {
                Emit(_recognizer.Result(), isFinal: true);
            }
            else
            {
                Emit(_recognizer.PartialResult(), isFinal: false);
            }
        }
        catch (Exception)
        {
            // Skip transient decode errors rather than tearing down the session.
        }
    }

    public void SetHotwords(IReadOnlyList<string> words)
    {
        // Biasing is applied up-front via the constructor's grammar; Vosk cannot
        // change the grammar of a live recognizer without rebuilding it.
    }

    internal static string? BuildGrammar(IReadOnlyList<string>? vocabulary)
    {
        if (vocabulary is null || vocabulary.Count == 0)
        {
            return null;
        }

        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string word in vocabulary)
        {
            if (!string.IsNullOrWhiteSpace(word))
            {
                distinct.Add(word.ToLowerInvariant());
            }
        }

        if (distinct.Count == 0 || distinct.Count > MaxGrammarWords)
        {
            return null; // too large for grammar mode; use open vocabulary
        }

        var entries = distinct.ToList();
        entries.Add("[unk]");
        return JsonSerializer.Serialize(entries, GrammarJson);
    }

    private void Emit(string json, bool isFinal)
    {
        string text = ExtractText(json, isFinal ? "text" : "partial");
        text = CleanUnknowns(text);

        // Vosk repeats the same partial for every ~20 ms audio chunk; only a
        // changed one is news for the matcher and the UI.
        if (!isFinal && string.Equals(text, _lastPartial, StringComparison.Ordinal))
        {
            return;
        }

        _lastPartial = isFinal ? string.Empty : text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            HypothesisReceived?.Invoke(this, new SpeechHypothesis(text, isFinal));
        }
    }

    private static string CleanUnknowns(string text)
    {
        if (text.Length == 0 || !text.Contains("[unk]", StringComparison.Ordinal))
        {
            return text;
        }

        return string.Join(' ', text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !string.Equals(w, "[unk]", StringComparison.Ordinal)));
    }

    private static string ExtractText(string json, string key)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(key, out JsonElement value)
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        _recognizer.Dispose();
        _model.Dispose();
    }
}
