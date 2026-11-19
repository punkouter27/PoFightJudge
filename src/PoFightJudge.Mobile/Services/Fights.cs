using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PoFightJudge.Shared;
using PoFightJudge.Shared.Identifiers;
using PoFightJudge.Shared.Models;

namespace PoFightJudge.Mobile.Services;

/// <summary>
/// A live 2P fight: the phone's microphone (echo-cancelled, 100 ms frames of 16 kHz PCM) goes up the live hub, the
/// host's voice (24 kHz PCM) comes back and plays, captions and snapshots drive the screen, and the keep-alive
/// (item 1) holds it all together with the screen locked.
/// </summary>
public sealed class LiveFight(ApiClient api, SecureSessionStore tokens, CookieContainer cookies, IMicrophone mic, ISpeaker speaker, IFightKeepAlive keepAlive) : IAsyncDisposable
{
    public const int HostRate = 24_000;
    private HubConnection? _hub;

    public MatchId? Id { get; private set; }

    public event Action<CaptionDto>? Caption;

    public event Action<DebateSnapshotDto>? Snapshot;

    public event Action? Interrupted;

    public event Action? Ended;

    public event Action<string>? Problem;

    public async Task<string?> StartAsync(MatchId id)
    {
        if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
        {
            return "A fight needs the microphone.";
        }

        Id = id;
        _hub = new HubConnectionBuilder()
            .WithUrl(api.U(ApiRoutes.Hubs.Live), options =>
            {
                options.AccessTokenProvider = tokens.GetBearerTokenAsync;
                options.Cookies = cookies;
                options.Headers["User-Agent"] = "PoFightJudge.Mobile/1.0";
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, MobileJsonContext.Default))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<byte[]>(LiveHubContract.AudioOut, speaker.Enqueue);
        _hub.On(LiveHubContract.AudioClear, () =>
        {
            speaker.Clear();
            Haptics.Interrupt();
            Interrupted?.Invoke();
        });
        _hub.On<CaptionDto>(LiveHubContract.Caption, c => Caption?.Invoke(c));
        _hub.On<DebateSnapshotDto>(LiveHubContract.Snapshot, s => Snapshot?.Invoke(s));
        _hub.On<string>(LiveHubContract.Ended, _ => Ended?.Invoke());
        _hub.On<string>(LiveHubContract.Error, m => Problem?.Invoke(m));
        _hub.Reconnecting += _ =>
        {
            Problem?.Invoke("Connection dropped — reconnecting…");
            return Task.CompletedTask;
        };
        _hub.Reconnected += async _ => await _hub.InvokeAsync(LiveHubContract.JoinFight, id.Value);

        try
        {
            await _hub.StartAsync();
            await _hub.InvokeAsync(LiveHubContract.JoinFight, id.Value);
        }
        catch (Exception ex) when (ex is HttpRequestException or HubException or InvalidOperationException)
        {
            return $"Couldn't join the fight: {ex.Message}";
        }

        keepAlive.Start("Fight in progress — the host is listening");
        speaker.StartStream(HostRate);
        if (!await mic.StartAsync(frame => _ = SendFrameAsync(frame), voiceProcessing: true))
        {
            await StopLocalAsync();
            return "The microphone couldn't be opened.";
        }

        return null;
    }

    private async Task SendFrameAsync(byte[] frame)
    {
        try
        {
            if (_hub?.State == HubConnectionState.Connected)
            {
                await _hub.SendAsync(LiveHubContract.AudioIn, frame);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HubException)
        {
        }
    }

    public async Task EndAsync()
    {
        try
        {
            if (_hub?.State == HubConnectionState.Connected)
            {
                await _hub.InvokeAsync(LiveHubContract.EndFight);
            }
            else if (Id is { } id)
            {
                await api.EndFightAsync(id);
            }
        }
        catch (Exception ex) when (ex is HubException or InvalidOperationException)
        {
        }

        await StopLocalAsync();
    }

    public async Task StopLocalAsync()
    {
        mic.Stop();
        speaker.StopStream();
        keepAlive.Stop();
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
    }

    public ValueTask DisposeAsync() => new(StopLocalAsync());
}

/// <summary>One fight recorded on the phone, waiting to be (or already) uploaded.</summary>
public sealed record PendingFight(RecordedFightRequest Meta, string Folder, string? Problem = null)
{
    public string AudioPath => Path.Combine(Folder, "fight.wav");
}

/// <summary>
/// Item 2: a fight recorded with no signal (or no host). The audio is written straight to a WAV on the phone with the
/// turn timeline beside it, and uploaded whenever there is a connection — the server picks the analysis up from there,
/// and the match id chosen here makes a retried upload safe.
/// </summary>
public sealed class RecordedFights(ApiClient api, IMicrophone mic, IFightKeepAlive keepAlive) : IDisposable
{
    private static readonly string Root = Directory.CreateDirectory(Path.Combine(FileSystem.AppDataDirectory, "recorded")).FullName;
    private readonly List<RecordedTurnDto> _turns = [];
    private readonly SemaphoreSlim _uploading = new(1, 1);
    private FileStream? _file;
    private long _bytes;
    private Speaker _floor = Speaker.Player1;
    private double _floorSince;

    public event Action? Changed;

    public bool Recording => _file is not null;

    public double Seconds => _bytes / 2.0 / Wav.Rate;

    public Speaker Floor => _floor;

    public async Task<string?> StartAsync()
    {
        if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
        {
            return "Recording needs the microphone.";
        }

        string folder = Directory.CreateDirectory(Path.Combine(Root, "current")).FullName;
        _file = new FileStream(Path.Combine(folder, "fight.wav"), FileMode.Create, FileAccess.Write);
        await _file.WriteAsync(Wav.Header(0)); // sizes patched on stop
        _bytes = 0;
        _turns.Clear();
        _floor = Speaker.Player1;
        _floorSince = 0;
        keepAlive.Start("Recording a fight");
        return await mic.StartAsync(OnFrame, voiceProcessing: false) ? null : "The microphone couldn't be opened.";
    }

    private void OnFrame(byte[] frame)
    {
        if (_file is { } f)
        {
            lock (f)
            {
                f.Write(frame);
                _bytes += frame.Length;
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Gives the floor to the other fighter; the turn so far is recorded.</summary>
    public void Switch(Speaker to)
    {
        if (to == _floor)
        {
            return;
        }

        double now = Seconds;
        if (now - _floorSince > 0.2)
        {
            _turns.Add(new RecordedTurnDto(_floor, _floorSince, now));
        }

        _floor = to;
        _floorSince = now;
        Haptics.Tap();
    }

    /// <summary>Stops, seals the WAV and keeps it with its meta until it is uploaded.</summary>
    public async Task<PendingFight?> StopAsync(string p1Tag, string p2Tag, string? p1Name, string? p2Name, string? topic, Stance? stance, DateTimeOffset startedAt)
    {
        mic.Stop();
        keepAlive.Stop();
        if (_file is not { } f)
        {
            return null;
        }

        _turns.Add(new RecordedTurnDto(_floor, _floorSince, Seconds));
        lock (f)
        {
            f.Position = 0;
            f.Write(Wav.Header((int)_bytes));
        }

        await f.DisposeAsync();
        _file = null;

        MatchId id = MatchId.New();
        string folder = Path.Combine(Root, id.Value);
        Directory.Move(Path.Combine(Root, "current"), folder);
        RecordedFightRequest meta = new(id, p1Tag, p2Tag, startedAt, [.. _turns], p1Name, p2Name, topic, stance);
        await File.WriteAllTextAsync(Path.Combine(folder, "meta.json"), JsonSerializer.Serialize(meta, MobileJsonContext.Default.RecordedFightRequest));
        Changed?.Invoke();
        return new PendingFight(meta, folder);
    }

    public static IReadOnlyList<PendingFight> Waiting() =>
        [.. Directory.EnumerateDirectories(Root).Where(d => Path.GetFileName(d) != "current" && File.Exists(Path.Combine(d, "meta.json")))
            .Select(d => JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(d, "meta.json")), MobileJsonContext.Default.RecordedFightRequest) is { } m
                ? new PendingFight(m, d, File.Exists(Path.Combine(d, "problem.txt")) ? File.ReadAllText(Path.Combine(d, "problem.txt")) : null)
                : null)
            .OfType<PendingFight>()];

    /// <summary>Uploads every waiting fight; returns the ids that are now on the server. Stops at the first outage.</summary>
    public async Task<List<MatchId>> UploadWaitingAsync()
    {
        List<MatchId> done = [];
        if (!ApiClient.Online || !await _uploading.WaitAsync(0))
        {
            return done;
        }

        try
        {
            foreach (PendingFight fight in Waiting().Where(f => f.Problem is null))
            {
                string? problem = await api.UploadRecordedAsync(fight.Meta, fight.AudioPath);
                if (problem is null)
                {
                    Directory.Delete(fight.Folder, recursive: true);
                    done.Add(fight.Meta.MatchId);
                }
                else
                {
                    // Refused for good (bad tags, bad audio): kept with the reason, so it can be fixed or deleted, not retried forever.
                    await File.WriteAllTextAsync(Path.Combine(fight.Folder, "problem.txt"), problem);
                }
            }
        }
        catch (HttpRequestException)
        {
        }
        finally
        {
            _uploading.Release();
            Changed?.Invoke();
        }

        return done;
    }

    public void Dispose()
    {
        _file?.Dispose();
        _uploading.Dispose();
    }

    public static void Discard(PendingFight fight) => Directory.Delete(fight.Folder, recursive: true);
}

/// <summary>Item 8: picks the opponent from the phone's contacts — their name, a three-letter tag, and how to reach them.</summary>
public static class Opponents
{
    public sealed record Picked(string Name, string Tag, string? Phone, string? Email);

    public static async Task<Picked?> PickAsync()
    {
        try
        {
            if (await Permissions.RequestAsync<Permissions.ContactsRead>() != PermissionStatus.Granted || await Contacts.Default.PickContactAsync() is not { } contact)
            {
                return null;
            }

            string name = contact.DisplayName?.Trim() is { Length: > 0 } n ? n : $"{contact.GivenName} {contact.FamilyName}".Trim();
            return new Picked(name, TagFor(name), contact.Phones.FirstOrDefault()?.PhoneNumber, contact.Emails.FirstOrDefault()?.EmailAddress);
        }
        catch (Exception ex) when (ex is FeatureNotSupportedException or PermissionException)
        {
            return null;
        }
    }

    /// <summary>Three letters: initials first ("Ann Marie Bell" → AMB), padded from the name ("Bob" → BOB, "Al" → ALX).</summary>
    public static string TagFor(string name)
    {
        string[] words = [.. name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => new string([.. w.Where(char.IsAsciiLetter)])).Where(w => w.Length > 0)];
        string tag = words.Length >= 3 ? string.Concat(words.Take(3).Select(w => w[0])) : string.Concat(words.SelectMany(w => w));
        tag = (tag.Length >= 3 ? tag[..3] : tag.PadRight(3, 'X')).ToUpperInvariant();
        return tag;
    }

    /// <summary>Sends the ruling's link to the opponent: SMS when there is a number, otherwise the share sheet.</summary>
    public static async Task SendRulingAsync(Picked? to, string url, string summary)
    {
        string text = $"{summary} {url}";
        if (to?.Phone is { Length: > 0 } phone && Sms.Default.IsComposeSupported)
        {
            await Sms.Default.ComposeAsync(new SmsMessage(text, [phone]));
            return;
        }

        await Share.Default.RequestAsync(new ShareTextRequest { Title = "The ruling", Text = summary, Uri = url });
    }
}
