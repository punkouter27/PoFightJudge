// PoSfx — every sound in this app that is not a voice, made up on the spot out of oscillators and noise. Nothing
// here is a file: a bell is four inharmonic partials on an exponential decay, a slap is a filtered noise burst with
// a pitch drop under it. That keeps the download at zero bytes and lets a sound take a parameter — the counting
// blip climbs with the number it is counting.
//
// Kept apart from PoAudio (a watch's synthesized lines) and PoLive (a fight's host), which own their own contexts.
// This one owns the third: a master gain into a compressor, so a bell landing under a voice cannot clip the mix.
// When an effect wants to be heard over a voice it asks those two to duck, which they do on their own bus.
//
// Muting is read from localStorage here rather than waited for from .NET: a sound cannot be taken back once it has
// been played, so the very first one has to know. Same key shape as theme.js, same tolerance for a quoted value.
window.PoSfx = (function () {
  "use strict";

  const STORAGE_KEY = "po-sound";
  const VOLUME_KEY = "po-volume";
  const OFF = "off";
  const MASTER_GAIN = 0.7;
  const NOISE_SECONDS = 2;

  // Gains ramp exponentially, and an exponential ramp can neither reach nor pass through zero, so silence is a very
  // small number instead. Below about 1e-4 nothing is audible on any output.
  const SILENT = 0.0001;

  let ctx = null;
  let master = null;
  let noise = null;
  let muted = readMuted();

  // Where the sound being scheduled right now goes: the master, or a panner in front of it for an effect that has a
  // side. Swapped for the length of one voice's call — every voice schedules all its nodes synchronously.
  let out = null;

  // One volume for everything the app plays: the effects here, and the voices in audio.js and live-audio.js, which
  // listen for the "po-volume" event. Kept here, and read at load like the mute, so the first sound is already at
  // the level somebody chose last time.
  let volume = readVolume();

  function readVolume() {
    try {
      const stored = parseFloat((localStorage.getItem(VOLUME_KEY) || "").replace(/^"|"$/g, ""));
      return stored >= 0 && stored <= 1 ? stored : 1;
    } catch (e) {
      return 1;
    }
  }
  let broken = false;

  function readMuted() {
    try {
      let stored = localStorage.getItem(STORAGE_KEY);
      if (stored) {
        stored = stored.replace(/^"|"$/g, "");
      }

      return stored === OFF;
    } catch (e) {
      return false;
    }
  }

  // The context is built on the first sound, never at load: one created before a gesture starts suspended, and the
  // browser logs a warning about it on every page of the app whether or not anything ever plays.
  function ensure() {
    if (broken) {
      return null;
    }

    try {
      if (!ctx) {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        ctx = new Ctor();
        const limiter = ctx.createDynamicsCompressor();
        limiter.threshold.value = -10;
        limiter.ratio.value = 12;
        limiter.attack.value = 0.003;
        limiter.release.value = 0.12;
        master = ctx.createGain();
        master.gain.value = muted ? 0 : MASTER_GAIN * volume;
        master.connect(limiter).connect(ctx.destination);
      }

      if (ctx.state === "suspended") {
        ctx.resume().catch(function () { /* still waiting on a gesture */ });
      }

      return ctx;
    } catch (e) {
      // No Web Audio at all (an old browser, a locked-down test host): every sound from here on is a no-op.
      broken = true;
      return null;
    }
  }

  /** White noise, made once and re-used: every percussive sound here is a filtered slice of the same two seconds. */
  function noiseBuffer() {
    if (!noise) {
      noise = ctx.createBuffer(1, ctx.sampleRate * NOISE_SECONDS, ctx.sampleRate);
      const data = noise.getChannelData(0);
      for (let i = 0; i < data.length; i++) {
        data[i] = Math.random() * 2 - 1;
      }
    }

    return noise;
  }

  /** One oscillator with an attack/decay envelope, optionally sweeping in pitch. Fire and forget. */
  function tone(at, options) {
    const o = ctx.createOscillator();
    const g = ctx.createGain();
    const duration = options.duration;
    o.type = options.type || "sine";
    o.frequency.setValueAtTime(options.freq, at);
    if (options.sweepTo) {
      o.frequency.exponentialRampToValueAtTime(Math.max(options.sweepTo, 1), at + duration);
    }

    const peak = Math.max(options.gain, SILENT * 2);
    const attack = options.attack || 0.004;
    g.gain.setValueAtTime(SILENT, at);
    g.gain.exponentialRampToValueAtTime(peak, at + attack);
    g.gain.exponentialRampToValueAtTime(SILENT, at + duration);

    o.connect(g).connect(out || master);
    o.start(at);
    o.stop(at + duration + 0.02);
  }

  /** A slice of the noise buffer through one filter — the transient half of anything that hits something. */
  function hit(at, options) {
    const src = ctx.createBufferSource();
    src.buffer = noiseBuffer();
    const filter = ctx.createBiquadFilter();
    filter.type = options.filter || "bandpass";
    filter.frequency.setValueAtTime(options.freq, at);
    if (options.sweepTo) {
      filter.frequency.exponentialRampToValueAtTime(Math.max(options.sweepTo, 20), at + options.duration);
    }

    if (options.q) {
      filter.Q.value = options.q;
    }

    const g = ctx.createGain();
    g.gain.setValueAtTime(Math.max(options.gain, SILENT * 2), at);
    g.gain.exponentialRampToValueAtTime(SILENT, at + options.duration);
    src.connect(filter).connect(g).connect(out || master);
    src.start(at, Math.random() * (NOISE_SECONDS - options.duration - 0.05));
    src.stop(at + options.duration + 0.02);
  }

  let room = null;

  /**
   * A courtroom, made up: a convolver fed 1.8 s of stereo noise falling away steeply, which is what a big hard room
   * does to a hit. Built once, on the first sound that wants it.
   */
  function reverb() {
    if (!room) {
      const length = Math.floor(ctx.sampleRate * 1.8);
      const impulse = ctx.createBuffer(2, length, ctx.sampleRate);
      for (let channel = 0; channel < 2; channel++) {
        const data = impulse.getChannelData(channel);
        for (let i = 0; i < length; i++) {
          data[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / length, 3.2);
        }
      }

      room = ctx.createConvolver();
      room.buffer = impulse;
      const wet = ctx.createGain();
      wet.gain.value = 0.45;
      room.connect(wet).connect(master);
    }

    return room;
  }

  /** Where a sound goes to be heard in the room: dry to wherever it was going, and a send into the reverb. */
  function roomy() {
    const split = ctx.createGain();
    split.connect(out || master);
    split.connect(reverb());
    return split;
  }

  // Staircase curves for the bit crusher, by number of steps: every sample rounded to one of a few levels.
  const crushCurves = {};

  function crushCurve(steps) {
    if (!crushCurves[steps]) {
      const curve = new Float32Array(1024);
      for (let i = 0; i < curve.length; i++) {
        const x = (i / (curve.length - 1)) * 2 - 1;
        curve[i] = Math.round(x * steps) / steps;
      }

      crushCurves[steps] = curve;
    }

    return crushCurves[steps];
  }

  /**
   * A chirp through a bit crusher, cut off rather than faded: the sound of a signal breaking up. The hard stop clicks,
   * and the click is the point — digital failure does not decay.
   */
  function crushed(at, options) {
    const o = ctx.createOscillator();
    const shaper = ctx.createWaveShaper();
    const g = ctx.createGain();
    o.type = options.type || "sawtooth";
    o.frequency.setValueAtTime(options.freq, at);
    o.frequency.exponentialRampToValueAtTime(Math.max(options.sweepTo || options.freq, 1), at + options.duration);
    shaper.curve = crushCurve(options.steps || 3);
    g.gain.setValueAtTime(options.gain, at);
    g.gain.setValueAtTime(0, at + options.duration);
    o.connect(shaper).connect(g).connect(out || master);
    o.start(at);
    o.stop(at + options.duration + 0.02);
  }

  /**
   * A Shepard–Risset rise: six sines an octave apart, all gliding up one octave together, each loudest in the middle
   * of the range and silent at both ends. The top one fades out as the bottom one fades in, so the ear hears a climb
   * that never arrives anywhere — which is what the last half-minute of an argument feels like. The gain curves are
   * the illusion; the gain's own value is zeroed first rather than scheduled, because a curve may not overlap an event.
   */
  function shepard(at, seconds, gain) {
    const partials = 6;
    const base = 55;
    const points = 64;
    for (let i = 0; i < partials; i++) {
      const o = ctx.createOscillator();
      const g = ctx.createGain();
      o.type = "sine";
      o.frequency.setValueAtTime(base * Math.pow(2, i), at);
      o.frequency.exponentialRampToValueAtTime(base * Math.pow(2, i + 1), at + seconds);

      const curve = new Float32Array(points);
      for (let k = 0; k < points; k++) {
        const t = k / (points - 1);
        const place = (i + t) / partials;
        const bell = Math.exp(-Math.pow(place - 0.5, 2) / 0.045);
        const swell = Math.min(1, t * 5, (1 - t) * 3);
        curve[k] = gain * bell * swell;
      }

      g.gain.value = 0;
      g.gain.setValueCurveAtTime(curve, at, seconds);
      o.connect(g).connect(out || master);
      o.start(at);
      o.stop(at + seconds + 0.05);
    }
  }

  /** A boxing bell: four inharmonic partials, struck. The ratios are what stop it sounding like a doorbell. */
  function bell(at, gain) {
    const ratios = [1, 1.51, 2.31, 3.07];
    const gains = [1, 0.5, 0.32, 0.18];
    for (let i = 0; i < ratios.length; i++) {
      tone(at, { type: "sine", freq: 660 * ratios[i], duration: 1.6 - i * 0.25, gain: gain * gains[i], attack: 0.002 });
    }

    hit(at, { freq: 5200, duration: 0.04, gain: gain * 0.5, q: 0.8 });
  }

  function gavel(at, gain) {
    hit(at, { freq: 1100, duration: 0.05, gain: gain, filter: "lowpass" });
    tone(at, { type: "triangle", freq: 190, sweepTo: 110, duration: 0.16, gain: gain * 0.8 });
  }

  /** A short run of notes. Playing them from one call is what makes a motif read as one gesture. */
  function motif(at, notes, type, gain, step, duration) {
    for (let i = 0; i < notes.length; i++) {
      tone(at + i * step, { type: type, freq: notes[i], duration: duration, gain: gain, attack: 0.008 });
    }
  }

  // The catalogue. A name that is not here plays nothing — an old page asking for a sound this build does not have
  // is not worth an exception. `value` is 0–1 and only means anything to the voices that read it.
  const VOICES = {
    "bell": function (at) { bell(at, 0.5); },
    "bell3": function (at) { bell(at, 0.45); bell(at + 0.3, 0.45); bell(at + 0.6, 0.5); },
    "slap": function (at) {
      hit(at, { freq: 2600, duration: 0.09, gain: 0.9, q: 1.1 });
      hit(at, { freq: 6000, duration: 0.03, gain: 0.5, filter: "highpass" });
      tone(at, { type: "sine", freq: 220, sweepTo: 48, duration: 0.18, gain: 0.7 });
    },
    "gavel": function (at) { gavel(at, 0.7); },
    "gavel3": function (at) { gavel(at, 0.6); gavel(at + 0.22, 0.6); gavel(at + 0.44, 0.75); },
    "tick": function (at) { tone(at, { type: "sine", freq: 1400, duration: 0.03, gain: 0.09 }); },
    "whoosh": function (at) { hit(at, { freq: 300, sweepTo: 3400, duration: 0.38, gain: 0.35, q: 2.4 }); },
    "stinger-intro": function (at) { motif(at, [392, 523.3, 659.3], "square", 0.16, 0.09, 0.2); },
    "stinger-probe": function (at) { motif(at, [587.3, 440], "triangle", 0.2, 0.11, 0.24); },
    "stinger-verdict": function (at) {
      motif(at, [523.3, 659.3, 784, 1046.5], "sawtooth", 0.12, 0.07, 0.22);
      bell(at + 0.34, 0.34);
    },
    "fanfare": function (at) {
      motif(at, [523.3, 659.3, 784, 1046.5, 1318.5], "square", 0.14, 0.075, 0.3);
      bell(at + 0.42, 0.4);
    },
    "clipping": function (at) {
      tone(at, { type: "sine", freq: 1800, duration: 0.05, gain: 0.22 });
      tone(at + 0.08, { type: "sine", freq: 1800, duration: 0.05, gain: 0.22 });
    },
    "cue": function (at) { tone(at, { type: "sine", freq: 660, sweepTo: 880, duration: 0.26, gain: 0.13, attack: 0.03 }); },
    // The one voice the caller shapes: a scoreboard counting up plays this per tick, climbing with the number.
    "count": function (at, value) { tone(at, { type: "sine", freq: 440 + value * 620, duration: 0.035, gain: 0.06 }); },
    // Three stutters of the same crushed chirp, each lower and the last cut short, over a hiss of dropped packets.
    "glitch": function (at) {
      for (let i = 0; i < 3; i++) {
        crushed(at + i * 0.055, { freq: 1500 - i * 420, sweepTo: 160, duration: i === 2 ? 0.028 : 0.045, gain: 0.14, steps: 4 });
      }

      hit(at, { freq: 6500, duration: 0.17, gain: 0.1, filter: "highpass" });
    },
    // The beat before somebody is named: a snare roll that speeds up and swells, a tom under every other stroke, and
    // a crash where it lands. 1.75 s to the crash, which is where Verdict.RevealAt names the winner.
    "drumroll": function (at) {
      const roll = 1.7;
      let t = 0;
      let stroke = 0;
      while (t < roll) {
        const swell = t / roll;
        hit(at + t, { freq: 1900, duration: 0.07, gain: 0.12 + swell * 0.3, q: 0.8 });
        if (stroke % 2 === 0) {
          tone(at + t, { type: "triangle", freq: 180, sweepTo: 150, duration: 0.05, gain: 0.05 + swell * 0.08 });
        }

        t += 0.085 - swell * 0.05;
        stroke++;
      }

      hit(at + 1.75, { freq: 6000, duration: 1.4, gain: 0.35, filter: "highpass" });
    },
    // The gavel coming down on a ruling, in a courtroom: a deep drop, the crack of wood, and the room answering.
    "slam": function (at) {
      out = roomy();
      tone(at, { type: "sine", freq: 95, sweepTo: 36, duration: 0.55, gain: 0.95 });
      hit(at, { freq: 900, duration: 0.12, gain: 0.8, filter: "lowpass" });
      hit(at, { freq: 2400, duration: 0.03, gain: 0.45, q: 1.2 });
    },
    // Something breaking like glass: a sharp crack as it splits, the break itself as it gives (when PoParticles lets
    // the shards go), then the pieces landing — nine pings at random, each one high, short and gone, in the same room.
    "shatter": function (at) {
      out = roomy();
      hit(at, { freq: 4200, duration: 0.04, gain: 0.45, q: 3 });
      at += 0.3;
      hit(at, { freq: 3200, duration: 0.25, gain: 0.5, filter: "highpass" });
      for (let i = 0; i < 9; i++) {
        const t = at + 0.02 + Math.random() * 0.5;
        tone(t, { type: "sine", freq: 2500 + Math.random() * 4500, duration: 0.08 + Math.random() * 0.15, gain: 0.05 });
        hit(t, { freq: 5000 + Math.random() * 3000, duration: 0.03, gain: 0.12, q: 4 });
      }
    },
    // Up a tier: a quick climb of five notes, and a shimmer of high pings over the top of it.
    "levelup": function (at) {
      motif(at, [523.3, 659.3, 784, 1046.5, 1318.5], "triangle", 0.15, 0.06, 0.25);
      for (let i = 0; i < 6; i++) {
        tone(at + 0.3 + i * 0.04, { type: "sine", freq: 2637 + i * 220, duration: 0.18, gain: 0.04 });
      }
    },
    // A rubber stamp coming down on paper: a short thunk, the knock of the handle, and the paper itself. `value`
    // pitches the thunk, so each fallacy lands with its own note.
    "stamp": function (at, value) {
      tone(at, { type: "sine", freq: 115 + value * 75, sweepTo: 55, duration: 0.14, gain: 0.55 });
      hit(at, { freq: 700, duration: 0.06, gain: 0.5, filter: "lowpass" });
      hit(at + 0.012, { freq: 4500, duration: 0.09, gain: 0.12, filter: "highpass" });
    },
    // A crowd on its feet, made up: two bands of noise swelling and falling away under a flutter (many voices are
    // never steady), about forty claps scattered over it, thickest in the middle, and one whistle near the start.
    "crowd": function (at) {
      const length = 3.2;
      [[520, 0.6, 0.22], [1350, 0.9, 0.12]].forEach(function (band) {
        const src = ctx.createBufferSource();
        src.buffer = noiseBuffer();
        src.loop = true;
        const filter = ctx.createBiquadFilter();
        filter.type = "bandpass";
        filter.frequency.value = band[0];
        filter.Q.value = band[1];
        const swell = ctx.createGain();
        swell.gain.setValueAtTime(SILENT, at);
        swell.gain.exponentialRampToValueAtTime(band[2], at + 0.5);
        swell.gain.setValueAtTime(band[2], at + 1.2);
        swell.gain.exponentialRampToValueAtTime(SILENT, at + length);
        const flutter = ctx.createGain();
        flutter.gain.value = 0.75;
        const lfo = ctx.createOscillator();
        lfo.frequency.value = 5.3 + Math.random() * 2;
        const depth = ctx.createGain();
        depth.gain.value = 0.25;
        lfo.connect(depth).connect(flutter.gain);
        src.connect(filter).connect(swell).connect(flutter).connect(out || master);
        src.start(at);
        src.stop(at + length + 0.05);
        lfo.start(at);
        lfo.stop(at + length + 0.05);
      });

      for (let i = 0; i < 40; i++) {
        // Bunched toward the middle of the swell: two uniforms averaged lean that way.
        const t = at + 0.2 + ((Math.random() + Math.random()) / 2) * (length - 0.6);
        hit(t, { freq: 1500 + Math.random() * 900, duration: 0.025, gain: 0.12 + Math.random() * 0.18, q: 1.4 });
      }

      tone(at + 0.6, { type: "sine", freq: 1800, sweepTo: 2600, duration: 0.25, gain: 0.05, attack: 0.03 });
      tone(at + 0.85, { type: "sine", freq: 2600, sweepTo: 2100, duration: 0.35, gain: 0.05, attack: 0.01 });
    },
    // Water sloshing across: a band of noise swept low to high and back, and a gulp that falls under it.
    "slosh": function (at) {
      hit(at, { freq: 380, sweepTo: 1200, duration: 0.22, gain: 0.3, q: 3 });
      hit(at + 0.16, { freq: 1100, sweepTo: 320, duration: 0.26, gain: 0.22, q: 3 });
      tone(at + 0.05, { type: "sine", freq: 340, sweepTo: 120, duration: 0.2, gain: 0.25 });
    },
    // A heart, lub-dub: two low sine thumps a fifth of a second apart, the second softer, with a soft knock on each.
    "heartbeat": function (at) {
      [[0, 0.8], [0.2, 0.55]].forEach(function (beat) {
        tone(at + beat[0], { type: "sine", freq: 70, sweepTo: 42, duration: 0.16, gain: beat[1], attack: 0.006 });
        hit(at + beat[0], { freq: 180, duration: 0.05, gain: beat[1] * 0.4, filter: "lowpass" });
      });
    },
    // A round ringing in, carried across the stage: the bell starts on one side and is swept to the other while it
    // rings. A stereo panner rather than the HRTF one, because this one moves.
    "bell-sweep": function (at) {
      if (typeof ctx.createStereoPanner === "function") {
        const pan = ctx.createStereoPanner();
        pan.pan.setValueAtTime(-0.9, at);
        pan.pan.linearRampToValueAtTime(0.9, at + 1.1);
        pan.connect(out || master);
        out = pan;
      }

      bell(at, 0.5);
      bell(at + 0.18, 0.3);
    },
    // The clock running out: under three seconds of endless climb, with a tick at the top to say it is done.
    "riser": function (at) {
      shepard(at, 2.6, 0.13);
      tone(at + 2.6, { type: "sine", freq: 1760, duration: 0.05, gain: 0.08 });
    },
  };

  /**
   * A panner for one sound, placed where audio.js places the voice on that side, or null for the middle. One per
   * play: it goes when the sound's sources do, and a slap on the left must not drag the next bell over with it.
   */
  function panned(pan) {
    if (typeof pan !== "number" || pan === 0 || typeof ctx.createPanner !== "function") {
      return null;
    }

    const panner = ctx.createPanner();
    panner.panningModel = "HRTF";
    panner.refDistance = 2;
    const x = Math.sign(pan) * 0.9;
    if (panner.positionX) {
      panner.positionX.value = x;
      panner.positionZ.value = -1;
    } else {
      panner.setPosition(x, 0, -1);
    }

    panner.connect(master);
    return panner;
  }

  /** Dips the voice buses so an effect lands over the top of a line instead of underneath it. */
  function duck(seconds) {
    const span = typeof seconds === "number" && seconds > 0 ? seconds : 0.35;
    const buses = [window.PoAudio, window.PoLive];
    for (let i = 0; i < buses.length; i++) {
      const bus = buses[i];
      if (bus && typeof bus.duck === "function") {
        try {
          bus.duck(span);
        } catch (e) {
          // A bus that will not duck still plays; the effect is simply level with it.
        }
      }
    }
  }

  return {
    /** Wakes the context on a user gesture, so the first real sound is not swallowed by the autoplay policy. */
    arm: function () {
      ensure();
    },

    /** The level everything plays at, 0 to 1. */
    volume: function () {
      return volume;
    },

    setVolume: function (value) {
      volume = Math.min(1, Math.max(0, Number(value) || 0));
      try {
        localStorage.setItem(VOLUME_KEY, String(volume));
      } catch (e) {
        // Storage blocked: it holds for this page.
      }

      if (ctx && !muted) {
        try {
          master.gain.setValueAtTime(MASTER_GAIN * volume, ctx.currentTime);
        } catch (e) {
          // A context that has gone away underneath us.
        }
      }

      window.dispatchEvent(new CustomEvent("po-volume", { detail: volume }));
    },

    setMuted: function (value) {
      muted = !!value;
      if (!ctx) {
        return;
      }

      try {
        if (muted) {
          // Whatever is already scheduled goes with it: silence that arrives half a second late is not silence.
          master.gain.cancelScheduledValues(ctx.currentTime);
          master.gain.setValueAtTime(0, ctx.currentTime);
        } else {
          master.gain.setValueAtTime(MASTER_GAIN * volume, ctx.currentTime);
        }
      } catch (e) {
        // Nothing scheduled, or a context that has gone away underneath us.
      }
    },

    /**
     * Plays one effect. `value` is 0–1 for the voices that take one; `pan` puts it on a side of the stage, below
     * zero the left and above it the right, the way the voices are placed. Silent when muted, when the name is
     * unknown, and when the browser has no Web Audio at all — an app that stopped because a bell would not ring is
     * worse than one that rings no bell. True when it was scheduled, so a test can find a voice that throws.
     */
    play: function (name, value, pan) {
      if (muted || typeof name !== "string") {
        return false;
      }

      const voice = VOICES[name];
      if (!voice || !ensure()) {
        return false;
      }

      try {
        out = panned(pan);
        voice(ctx.currentTime + 0.005, typeof value === "number" ? Math.min(Math.max(value, 0), 1) : 0);
        return true;
      } catch (e) {
        // One malformed schedule must not take the page with it.
        return false;
      } finally {
        out = null;
      }
    },

    /** Every voice there is, for the test that plays each one. */
    names: function () {
      return Object.keys(VOICES);
    },

    duck: duck,
  };
})();
