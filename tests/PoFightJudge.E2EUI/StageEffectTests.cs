using Microsoft.Playwright;

namespace PoFightJudge.E2EUI;

/// <summary>
/// The drawn and sounded layers, in a browser that actually has both. Nothing here looks at a picture: what these
/// prove is that the shaders compile and link, that a mounted canvas is really in the page, and that a browser
/// which cannot do any of it says so rather than throwing — every other tier can only assert the decision to ask.
/// </summary>
[Collection(AppCollection.Name)]
public class StageEffectTests(AppFixture app)
{
    /// <summary>
    /// A shader that will not compile mounts nothing, silently, and there is no other way to find out: the page
    /// keeps working, the canvas simply never appears. Software rendering is enough — this is about the GLSL.
    /// </summary>
    [SkippableFact]
    public async Task Every_shader_compiles_and_mounts_in_a_real_browser()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop);
        await using var _ = context;

        var probe = await page.EvaluateAsync<bool>("() => PoGfx.probe().webgl2");
        Skip.IfNot(probe, "this browser has no WebGL2 (headless software rendering is off)");

        var mounted = await page.EvaluateAsync<string[]>(
            """
            () => {
              const host = document.createElement('div');
              host.id = 'gfx-probe';
              host.style.cssText = 'position:fixed;left:0;top:0;width:200px;height:120px;';
              document.body.appendChild(host);
              const drawn = [];
              for (const shader of ['stage', 'backdrop', 'grain', 'fluid', 'rays', 'swarm']) {
                if (PoGfx.mount('#gfx-probe', shader)) { drawn.push(shader); }
                PoGfx.unmount('#gfx-probe');
              }
              host.remove();
              return drawn;
            }
            """);

        mounted.Should().Equal(["stage", "backdrop", "grain", "fluid", "rays", "swarm"], "every shader must compile and link, or it draws nothing at all");
        errors.Should().BeEmpty();
    }

    /// <summary>
    /// The live shader hands the voice it is breathing with to the page's CSS as <c>--po-level</c>, so the card of
    /// whoever is talking can glow with them. Fed here by a stand-in host bus.
    /// </summary>
    [SkippableFact]
    public async Task A_live_shader_publishes_the_voice_level_to_the_page()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop);
        await using var _ = context;
        Skip.IfNot(await page.EvaluateAsync<bool>("() => PoGfx.probe().webgl2"), "this browser has no WebGL2");

        var level = await page.EvaluateAsync<double>(
            """
            async () => {
              window.PoLive = { hostLevel: () => 0.8 };
              const host = document.createElement('div');
              host.id = 'gfx-level';
              host.style.cssText = 'position:fixed;left:0;top:0;width:200px;height:120px;';
              document.body.appendChild(host);
              PoGfx.mount('#gfx-level', 'backdrop', { level: 'live' });
              await new Promise(r => setTimeout(r, 600));
              const value = parseFloat(host.style.getPropertyValue('--po-level'));
              PoGfx.unmount('#gfx-level');
              return value;
            }
            """);

        level.Should().BeGreaterThan(0.5, "a voice at 0.8 eases up toward it within half a second");
        errors.Should().BeEmpty();
    }

    /// <summary>
    /// Nothing is drawn until somebody turns stage effects on, and somebody who asked the whole system for less motion
    /// is not given the loudest thing in it even when the switch is on.
    /// </summary>
    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Effects_off_or_reduced_motion_mounts_nothing(bool switchedOn, bool reducedMotion)
    {
        Skip.If(app.Browser is null, app.Unavailable);
        var context = await app.Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = AppFixture.Desktop,
            ReducedMotion = reducedMotion ? ReducedMotion.Reduce : ReducedMotion.NoPreference,
        });
        await using var _ = context;
        if (switchedOn)
        {
            await context.AddInitScriptAsync("try { localStorage.setItem('po-effects', 'on'); } catch (e) { }");
        }

        var page = await context.NewPageAsync();
        await page.GotoAsync(app.BaseUrl + "/login", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await page.WaitForFunctionAsync("() => !!window.PoGfx", null, new() { Timeout = 30_000 });

        var mounted = await page.EvaluateAsync<bool>(
            """
            () => {
              const host = document.createElement('div');
              host.id = 'gfx-probe';
              host.style.cssText = 'position:fixed;left:0;top:0;width:200px;height:120px;';
              document.body.appendChild(host);
              const drawn = PoGfx.mount('#gfx-probe', 'stage');
              const thrown = PoParticles.burst('#gfx-probe', 'confetti');
              PoGfx.unmount('#gfx-probe');
              PoParticles.clear('#gfx-probe');
              host.remove();
              return drawn || thrown;
            }
            """);

        mounted.Should().BeFalse(switchedOn ? "reduced motion outranks the switch" : "stage effects are off by default");
    }

    /// <summary>
    /// The two pictures drawn on a 2D canvas. The story picture is drawn at the size a story is, with a long topic
    /// and a slip on it. A clip becomes mountains: decoded in the browser, through the FFT, drawn onto its canvas —
    /// any one of those failing leaves the canvas blank and says nothing, so the pixels are what is checked.
    /// </summary>
    [SkippableFact]
    public async Task The_ruling_is_drawn_story_sized_and_a_clip_as_mountains()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop);
        await using var _ = context;

        var size = await page.EvaluateAsync<int[]>(
            """
            async () => {
              const url = await PoSave.story({
                headline: 'AL took it.', topic: 'whose turn it is to load the dishwasher, again, on a Sunday night', one: 'AL', two: 'SM',
                oneShare: 0.62, twoShare: 0.38, summary: 'One of them meant it. '.repeat(20), fallacy: 'Moving the goalposts',
                fallacyQuote: 'that is not what I meant', rehearsal: true, when: '19 October 2026', fileName: 'x.png' }, 'data');
              const image = new Image();
              await new Promise((resolve, reject) => { image.onload = resolve; image.onerror = reject; image.src = url; });
              return [image.naturalWidth, image.naturalHeight];
            }
            """);

        size.Should().Equal(1080, 1920);

        var lit = await page.EvaluateAsync<int>(
            """
            async () => {
              const rate = 16000, seconds = 0.5, samples = rate * seconds;
              const wav = new DataView(new ArrayBuffer(44 + samples * 2));
              const text = (at, s) => [...s].forEach((c, i) => wav.setUint8(at + i, c.charCodeAt(0)));
              text(0, 'RIFF'); wav.setUint32(4, 36 + samples * 2, true); text(8, 'WAVEfmt ');
              wav.setUint32(16, 16, true); wav.setUint16(20, 1, true); wav.setUint16(22, 1, true);
              wav.setUint32(24, rate, true); wav.setUint32(28, rate * 2, true); wav.setUint16(32, 2, true); wav.setUint16(34, 16, true);
              text(36, 'data'); wav.setUint32(40, samples * 2, true);
              for (let i = 0; i < samples; i++) { wav.setInt16(44 + i * 2, Math.sin(2 * Math.PI * 440 * i / rate) * 12000, true); }
              const bytes = new Uint8Array(wav.buffer);
              let binary = ''; bytes.forEach(b => binary += String.fromCharCode(b));

              const host = document.createElement('div');
              host.className = 'probe-terrain';
              host.innerHTML = '<canvas style="display:block;width:400px;height:120px"></canvas><audio src="data:audio/wav;base64,' + btoa(binary) + '"></audio>';
              document.body.appendChild(host);
              if (!PoTerrain.attach('.probe-terrain audio', '.probe-terrain canvas', '--po-p1')) { return -1; }

              await new Promise(r => setTimeout(r, 800));
              const canvas = host.querySelector('canvas');
              const pixels = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
              let count = 0;
              for (let i = 3; i < pixels.length; i += 4) { if (pixels[i] > 0) count++; }
              return count;
            }
            """);

        lit.Should().BeGreaterThan(1000, "a tone is a mountain, and a mountain is drawn");
        errors.Should().BeEmpty();
    }

    /// <summary>
    /// A stamp is armed off the page and lands the first time it is seen, not before: one in a tab nobody has opened
    /// must still be waiting when the tab is.
    /// </summary>
    [SkippableFact]
    public async Task A_stamp_waits_until_it_is_seen_and_then_lands()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop);
        await using var _ = context;

        await page.EvaluateAsync(
            """
            () => {
              const below = document.createElement('div');
              below.style.cssText = 'height:3000px';
              below.innerHTML = '<span class="probe-stamp" style="display:inline-block;margin-top:2800px">FALLACY</span>';
              document.body.appendChild(below);
              PoFx.stamps('.probe-stamp');
            }
            """);
        (await page.EvaluateAsync<string>("() => document.querySelector('.probe-stamp').className")).Should().Contain("armed");

        await page.EvaluateAsync("() => document.querySelector('.probe-stamp').scrollIntoView()");
        await page.WaitForFunctionAsync("() => document.querySelector('.probe-stamp').classList.contains('stamped')", null, new() { Timeout = 5_000 });
        errors.Should().BeEmpty();
    }

    /// <summary>
    /// Every synthesized sound schedules without throwing, and a voice placed on one side of the stage and an effect
    /// on the other go through an HRTF panner. A voice that throws is caught and is simply silent, for ever, and a
    /// panner is a node a browser can refuse to build, so this is the only place either would ever be noticed.
    /// </summary>
    [SkippableFact]
    public async Task Every_sound_schedules_and_can_be_placed_on_a_side_of_the_stage()
    {
        var (context, page, errors) = await app.OpenAsync(AppFixture.Desktop);
        await using var _ = context;

        var silent = await page.EvaluateAsync<string[]>("() => PoSfx.names().filter(n => !PoSfx.play(n, 0.5))");
        silent.Should().BeEmpty("a sound that cannot be scheduled is never heard and never reported");

        var seconds = await page.EvaluateAsync<double>(
            """
            async () => {
              const silence = btoa(String.fromCharCode(...new Uint8Array(4800)));
              const left = await PoAudio.enqueue(silence, 'pcm', -1);
              const right = await PoAudio.enqueue(silence, 'pcm', 1);
              PoSfx.play('slap', 0, 1);
              PoAudio.stop();
              return left + right;
            }
            """);

        seconds.Should().BeApproximately(0.2, 0.01, "two tenths of a second of 24 kHz silence, one from each side");
        errors.Should().BeEmpty();
    }
}
