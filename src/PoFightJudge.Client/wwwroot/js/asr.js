// The page's half of the browser transcriber (see asr-worker.js). warm() starts the one-off download in the
// background; transcribe() answers only once the model is ready, and null otherwise, so a turn never waits on a
// download — the clip goes to the server instead, as it always did.
window.PoAsr = (function () {
  "use strict";

  /** Longer than any honest turn takes on WASM; past it the server is asked instead. */
  const TIMEOUT_MS = 20000;

  let worker = null;
  let ready = false;
  let next = 0;
  const waiting = new Map();

  function warm() {
    if (worker || typeof Worker !== "function") {
      return;
    }

    try {
      worker = new Worker("js/asr-worker.js", { type: "module" });
    } catch (err) {
      worker = null;
      return;
    }

    worker.onmessage = function (e) {
      const msg = e.data || {};
      if (msg.type === "ready") {
        ready = true;
      } else if (msg.type === "failed") {
        console.warn("On-device transcription is off for this visit:", msg.error);
        worker.terminate();
        worker = null;
      } else if (msg.type === "text" && waiting.has(msg.id)) {
        waiting.get(msg.id)(msg.text);
        waiting.delete(msg.id);
      }
    };
    worker.onerror = function () {
      ready = false;
    };
    worker.postMessage({ type: "warm" });
  }

  function transcribe() {
    const samples = ready && worker && window.PoMic ? window.PoMic.samples() : null;
    if (!samples || samples.length === 0) {
      return Promise.resolve(null);
    }

    const id = ++next;
    return new Promise(function (resolve) {
      const timer = setTimeout(function () {
        waiting.delete(id);
        resolve(null);
      }, TIMEOUT_MS);
      waiting.set(id, function (text) {
        clearTimeout(timer);
        resolve(text);
      });
      worker.postMessage({ type: "run", id: id, samples: samples }, [samples.buffer]);
    });
  }

  return { warm: warm, transcribe: transcribe };
})();
