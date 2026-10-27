// One-shot stage effects for the play screen. Nothing here is load-bearing: every function is safe to no-op.
window.PoFx = (function () {
  "use strict";

  const DURATION = 700;
  let timer = 0;

  // The play screen's stage, unless the caller names something else — the verdict jolts its report.
  function target(selector) {
    return (selector && document.querySelector(selector)) || document.querySelector(".stage") || document.body;
  }

  // Re-triggers a CSS animation on the stage. Removing the class and forcing a reflow before adding it back is
  // what makes a second slap animate at all; without the reflow the browser coalesces the two class changes.
  function burst(kind, selector) {
    if (typeof kind !== "string" || kind.length === 0) {
      return;
    }

    const el = target(selector);
    const name = "fx-" + kind.replace(/[^a-z0-9-]/gi, "");
    el.classList.remove(name);
    void el.offsetWidth;
    el.classList.add(name);

    window.clearTimeout(timer);
    timer = window.setTimeout(function () {
      el.classList.remove(name);
    }, DURATION);
  }

  // Brings the first match into the middle of whatever scrolls it. Nothing happens when there is none.
  function reveal(selector) {
    const el = document.querySelector(selector);
    if (el) {
      el.scrollIntoView({ block: "center", behavior: "smooth" });
    }
  }

  function scrollBottom(el) {
    if (el) {
      el.scrollTop = el.scrollHeight;
    }
  }

  // Table mode, for a phone lying between two people: the class on <html> moves the app's own chrome out of the way
  // of the stage (app.css), and fullscreen — only when `full`, because only a click may ask for it — takes the
  // browser's too. A browser that will not go fullscreen (an iPhone has no element fullscreen) still gets the page.
  function table(on, full) {
    document.documentElement.classList.toggle("po-table", !!on);
    if (!full) {
      return;
    }

    try {
      if (on && !document.fullscreenElement && document.documentElement.requestFullscreen) {
        document.documentElement.requestFullscreen().catch(function () {});
      } else if (!on && document.fullscreenElement && document.exitFullscreen) {
        document.exitFullscreen().catch(function () {});
      }
    } catch (e) {
      // Not here, not now. The takeover does not depend on it.
    }
  }

  // The line a stamp lands on takes the hit: a short jolt, re-triggered the same way burst() re-triggers a slap.
  function thud(el) {
    if (!el) {
      return;
    }

    el.classList.remove("fx-thud");
    void el.offsetWidth;
    el.classList.add("fx-thud");
    window.setTimeout(function () { el.classList.remove("fx-thud"); }, 400);
  }

  // Each fallacy lands at its own pitch, so a second "strawman" sounds like the first and not like an ad hominem:
  // the name hashed to 0–1, which the stamp voice turns into a pitch.
  function pitchOf(name) {
    let hash = 7;
    for (let i = 0; i < name.length; i++) {
      hash = (hash * 31 + name.charCodeAt(i)) % 1009;
    }

    return hash / 1008;
  }

  // Stamps that drop onto the page the first time they are seen. Armed now (up off the page, waiting); stamped as
  // they come into view — a tab opened, a list scrolled to — one after another when several arrive together, each
  // with its thunk. A browser with no IntersectionObserver, or somebody who asked for less motion, gets them as they
  // are, and the class names are FallacyStamp.razor.css's.
  function stamps(selector) {
    let still = false;
    try {
      still = window.PoMotion.still();
    } catch (e) {
      still = false;
    }

    if (still || typeof IntersectionObserver !== "function") {
      return;
    }

    const observer = new IntersectionObserver(function (entries) {
      let order = 0;
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) {
          return;
        }

        observer.unobserve(entry.target);
        const el = entry.target;
        window.setTimeout(function () {
          el.classList.remove("armed");
          el.classList.add("stamped");
          thud(el.parentElement);
          if (window.PoSfx) {
            window.PoSfx.play("stamp", pitchOf(el.getAttribute("aria-label") || ""));
          }
        }, order++ * 180);
      });
    }, { threshold: 0.6 });

    document.querySelectorAll(selector).forEach(function (el) {
      if (!el.classList.contains("stamped")) {
        el.classList.add("armed");
        observer.observe(el);
      }
    });
  }

  // Holographic cards. Each one's --mx and --my (0 to 1 across it) ease toward a target every frame: the pointer while
  // it is over the card, the tilt of the phone where there is one, and otherwise a slow orbit so the foil is never
  // dead. One loop for all of them, and it stops when none is left in the page. Reduced motion: face on, still.
  const holos = [];
  let holoFrame = 0;
  let tilt = null;

  function holoStep(now) {
    holoFrame = 0;
    for (let i = holos.length - 1; i >= 0; i--) {
      const card = holos[i];
      if (!card.el.isConnected) {
        holos.splice(i, 1);
        continue;
      }

      let tx = 0.5 + Math.cos(now / 2600 + card.phase) * 0.28;
      let ty = 0.5 + Math.sin(now / 3100 + card.phase) * 0.22;
      if (card.pointer) {
        tx = card.pointer.x;
        ty = card.pointer.y;
      } else if (tilt) {
        tx = tilt.x;
        ty = tilt.y;
      }

      card.x += (tx - card.x) * 0.12;
      card.y += (ty - card.y) * 0.12;
      card.el.style.setProperty("--mx", card.x.toFixed(3));
      card.el.style.setProperty("--my", card.y.toFixed(3));
    }

    if (holos.length > 0 && !document.hidden) {
      holoFrame = requestAnimationFrame(holoStep);
    }
  }

  function holo(selector) {
    try {
      if (window.PoMotion.still()) {
        return;
      }
    } catch (e) {
      // No media queries: carry on.
    }

    document.querySelectorAll(selector).forEach(function (el) {
      if (holos.some(function (card) { return card.el === el; })) {
        return;
      }

      const card = { el: el, x: 0.5, y: 0.5, pointer: null, phase: holos.length * 2.1 };
      el.addEventListener("pointermove", function (e) {
        const box = el.getBoundingClientRect();
        card.pointer = { x: (e.clientX - box.left) / box.width, y: (e.clientY - box.top) / box.height };
      });
      el.addEventListener("pointerleave", function () { card.pointer = null; });
      holos.push(card);
    });

    // A phone held in the hand turns every card at once. Where the browser asks permission first (iOS), it is not
    // asked for: a card that only moves with the finger is fine.
    if (!tilt && typeof window.DeviceOrientationEvent === "function" && typeof DeviceOrientationEvent.requestPermission !== "function") {
      window.addEventListener("deviceorientation", function (e) {
        if (typeof e.gamma === "number" && typeof e.beta === "number") {
          tilt = { x: Math.min(1, Math.max(0, 0.5 + e.gamma / 60)), y: Math.min(1, Math.max(0, 0.5 + (e.beta - 45) / 60)) };
        }
      });
    }

    if (!holoFrame && holos.length > 0) {
      holoFrame = requestAnimationFrame(holoStep);
    }
  }

  document.addEventListener("visibilitychange", function () {
    if (!document.hidden && !holoFrame && holos.length > 0) {
      holoFrame = requestAnimationFrame(holoStep);
    }
  });

  return { burst: burst, scrollBottom: scrollBottom, reveal: reveal, table: table, stamps: stamps, holo: holo };
})();
