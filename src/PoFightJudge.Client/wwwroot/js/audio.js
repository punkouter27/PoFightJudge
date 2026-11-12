// PoAudio — the browser side of playback. One AudioContext, one rolling schedule clock, so consecutive chunks of a
// line butt up against each other instead of leaving a gap or overlapping. This is WATCH playback; a live fight
// captures through mic.js and plays the host through live-audio.js, which owns its own context and bus.
const PoAudio = (() => {
  const SAMPLE_RATE = 24000; // raw PCM from Gemini TTS / the fake is always 24 kHz mono 16-bit

  /** How far a line is dipped while an effect lands over it. Under it, not silenced: the words still matter. */
  const DUCK_TO = 0.32;
  let context = null;
  let bus = null;
  let out = null;

  window.addEventListener('pofightjudge:volume', (e) => {
    if (out) {
      out.gain.value = e.detail;
    }
  });
  let analyser = null;
  let levelData = null;
  let currentLevel = 0;
  let meterFrame = 0;
  let nextStartTime = 0;
  let sources = [];

  /** One panner per side of the stage, made once: every clause a side speaks goes through the same one. */
  const panners = {};

  function ensureContext() {
    if (!context) {
      const Ctor = window.AudioContext || window.webkitAudioContext;
      context = new Ctor();
      // Everything spoken goes through one gain rather than straight out, so PoSfx can dip the voice under a bell
      // without touching the sources that are already scheduled.
      bus = context.createGain();
      analyser = context.createAnalyser();
      analyser.fftSize = 512;
      analyser.smoothingTimeConstant = 0.35;
      levelData = new Uint8Array(analyser.fftSize);
      // The meter reads the voice before the volume control, so the aura breathes the same at any level.
      out = context.createGain();
      out.gain.value = window.PoSfx ? window.PoSfx.volume() : 1;
      bus.connect(analyser);
      analyser.connect(out);
      out.connect(context.destination);
    }
    if (context.state === 'suspended') {
      // Autoplay policy: a click has already happened by the time anything plays.
      context.resume();
    }
    return context;
  }

  // How loud the line being spoken is, right now. Published as a custom property so a card can glow with it without
  // anything crossing into .NET, and read straight off by PoGfx for the aura behind whoever is talking. The loop
  // only runs while something is actually scheduled: a silent page costs nothing.
  function pumpLevel() {
    if (!analyser || sources.length === 0) {
      currentLevel = 0;
      publishLevel(0);
      meterFrame = 0;
      return;
    }

    analyser.getByteTimeDomainData(levelData);
    let sum = 0;
    for (let i = 0; i < levelData.length; i++) {
      const v = (levelData[i] - 128) / 128;
      sum += v * v;
    }

    // The same curve the live meter uses: a linear RMS sits near zero for ordinary speech and says nothing.
    currentLevel = Math.min(1, Math.pow(Math.sqrt(sum / levelData.length) * 3.2, 0.7));
    publishLevel(currentLevel);
    meterFrame = requestAnimationFrame(pumpLevel);
  }

  function publishLevel(value) {
    try {
      document.documentElement.style.setProperty('--po-speak-level', value.toFixed(3));
    } catch {
      // No document (a worker, a test host): nothing glows, and nothing breaks.
    }
  }

  function base64ToBytes(base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
      bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
  }

  /** Raw 16-bit little-endian PCM has no container, so it is decoded by hand into a mono buffer. */
  function pcmToBuffer(ctx, bytes) {
    const samples = Math.floor(bytes.length / 2);
    const buffer = ctx.createBuffer(1, Math.max(samples, 1), SAMPLE_RATE);
    const channel = buffer.getChannelData(0);
    const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    for (let i = 0; i < samples; i++) {
      channel[i] = view.getInt16(i * 2, true) / 32768;
    }
    return buffer;
  }

  /**
   * Where a voice stands. The husband's card is on the left and the wife's on the right, so that is where they are
   * heard: an HRTF panner a little in front of the listener and a little apart, which on headphones places them in
   * the room rather than in one ear. The reference distance is past where they stand, so neither is quieter for it.
   * No side (or a browser with no panner) goes straight onto the bus, which is the middle it always was.
   */
  function inputFor(pan) {
    if (typeof pan !== 'number' || pan === 0 || typeof context.createPanner !== 'function') {
      return bus;
    }

    const key = pan < 0 ? 'left' : 'right';
    if (!panners[key]) {
      const panner = context.createPanner();
      panner.panningModel = 'HRTF';
      panner.distanceModel = 'inverse';
      panner.refDistance = 2;
      place(panner, Math.sign(pan) * 0.9, 0, -1);
      panner.connect(bus);
      panners[key] = panner;
    }

    return panners[key];
  }

  /** Safari before 14.1 has no positionX AudioParams, only the old setter. */
  function place(panner, x, y, z) {
    if (panner.positionX) {
      panner.positionX.value = x;
      panner.positionY.value = y;
      panner.positionZ.value = z;
    } else {
      panner.setPosition(x, y, z);
    }
  }

  async function toBuffer(ctx, base64, format) {
    const bytes = base64ToBytes(base64);
    if (format === 'pcm') {
      return pcmToBuffer(ctx, bytes);
    }
    // mp3 (and anything else the browser knows) goes through the platform decoder.
    return await ctx.decodeAudioData(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength));
  }

  return {
    /**
     * Plays one utterance immediately, replacing whatever was playing. Resolves with its duration in seconds —
     * once it is actually scheduled, which is the point: the caller waits out the line before moving on, and a
     * promise that resolved before the decode finished had the speaker cut off a second into their sentence.
     */
    async play(base64, format, pan) {
      if (!base64) {
        return 0;
      }
      PoAudio.stop();
      nextStartTime = ensureContext().currentTime;
      return await PoAudio.enqueue(base64, format, pan);
    },

    /**
     * Appends a chunk to the rolling schedule: each chunk starts where the previous one ends, so a line synthesized
     * clause by clause still plays as one continuous utterance. Returns the scheduled duration in seconds. `pan` is
     * which side of the stage it comes from: below zero the left, above it the right, zero or absent the middle.
     */
    async enqueue(base64, format, pan) {
      if (!base64) {
        return 0;
      }
      const ctx = ensureContext();
      const buffer = await toBuffer(ctx, base64, format);
      const source = ctx.createBufferSource();
      source.buffer = buffer;
      source.connect(inputFor(pan));
      const startAt = Math.max(nextStartTime, ctx.currentTime);
      source.start(startAt);
      nextStartTime = startAt + buffer.duration;
      sources.push(source);
      if (!meterFrame) {
        meterFrame = requestAnimationFrame(pumpLevel);
      }
      source.onended = () => {
        sources = sources.filter((s) => s !== source);
      };
      return buffer.duration;
    },

    /** Stops everything and resets the schedule clock. */
    stop() {
      for (const source of sources) {
        try {
          source.stop();
        } catch {
          // Already finished; nothing to stop.
        }
      }
      sources = [];
      nextStartTime = context ? context.currentTime : 0;
      currentLevel = 0;
      publishLevel(0);
    },

    /** How loud the current line is, 0 to 1 — what the aura behind the speaker breathes with. */
    level() {
      return currentLevel;
    },

    /**
     * Dips the spoken line for `seconds` and brings it back. Called by PoSfx, not by .NET: an effect and the duck
     * under it have to be scheduled on the same beat, and a round trip is longer than the effect.
     */
    duck(seconds) {
      if (!context || !bus) {
        return;
      }
      const now = context.currentTime;
      const span = seconds > 0 ? seconds : 0.35;
      bus.gain.cancelScheduledValues(now);
      bus.gain.setValueAtTime(bus.gain.value, now);
      bus.gain.linearRampToValueAtTime(DUCK_TO, now + 0.04);
      bus.gain.setValueAtTime(DUCK_TO, now + span);
      bus.gain.linearRampToValueAtTime(1, now + span + 0.25);
    },

    /** Seconds of audio still scheduled ahead of now. */
    pending() {
      return context ? Math.max(0, nextStartTime - context.currentTime) : 0;
    },

    /**
     * Reads text aloud in the browser's own voice — the verdict's recap, which needs no synthesis on the server and
     * costs nothing. Answers false where the browser has no voice, rather than throwing.
     */
    say(text) {
      if (!text || !window.speechSynthesis || typeof SpeechSynthesisUtterance === 'undefined') {
        return false;
      }
      speechSynthesis.cancel();
      const line = new SpeechSynthesisUtterance(text);
      line.rate = 1.1;
      speechSynthesis.speak(line);
      return true;
    },

    /** Stops whatever say() started. */
    hush() {
      window.speechSynthesis?.cancel();
    },
  };
})();

window.PoAudio = PoAudio;
