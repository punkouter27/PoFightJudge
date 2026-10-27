// PoParticles — the bits that fly off things. A 2D canvas per element, one pooled array, one frame loop that stops
// dead when nothing is alive, so a page that is not celebrating anything costs nothing at all.
//
// Deliberately not WebGL: a few hundred quads a frame is well inside what canvas 2D does without breaking sweat,
// and the code that survives is the physics rather than the plumbing. PoGfx is next door for the shaders.
//
// Reduced motion is honoured the same way it is there — nothing is spawned at all.
window.PoParticles = (function () {
  "use strict";

  /** Hard cap. Past this the frame cost stops being free and nobody can tell the difference anyway. */
  const MAX = 420;

  /** Pixels per second squared. Positive is down: the canvas has y running down the page. */
  const GRAVITY = 900;

  const stages = new Map();
  let frame = 0;
  let last = 0;

  function reducedMotion() {
    try {
      return window.PoMotion.still();
    } catch (e) {
      return false;
    }
  }

  /** A token's colour, resolved by the browser. PoGfx already owns the probe that unpicks light-dark(). */
  function colour(token, fallback) {
    try {
      if (window.PoGfx && typeof window.PoGfx.colour === "function") {
        return window.PoGfx.colour(token) || fallback;
      }
    } catch (e) {
      // Fall through.
    }

    return fallback;
  }

  function stageFor(selector) {
    let stage = stages.get(selector);
    if (stage) {
      return stage;
    }

    const host = document.querySelector(selector);
    if (!host) {
      return null;
    }

    const canvas = document.createElement("canvas");
    canvas.className = "po-particles";
    canvas.setAttribute("aria-hidden", "true");
    canvas.style.cssText = "position:absolute;inset:0;width:100%;height:100%;display:block;pointer-events:none;z-index:2;";
    let ctx = null;
    try {
      ctx = canvas.getContext("2d");
    } catch (e) {
      ctx = null;
    }

    if (!ctx) {
      return null;
    }

    host.appendChild(canvas);
    stage = { host: host, canvas: canvas, ctx: ctx, live: [], emitting: 0, emitCarry: 0, tint: ["#ffcc33"] };
    stages.set(selector, stage);
    return stage;
  }

  function resize(stage) {
    const rect = stage.host.getBoundingClientRect();
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const width = Math.max(1, Math.round(rect.width * dpr));
    const height = Math.max(1, Math.round(rect.height * dpr));
    if (stage.canvas.width !== width || stage.canvas.height !== height) {
      stage.canvas.width = width;
      stage.canvas.height = height;
    }

    return { width: width, height: height, dpr: dpr };
  }

  function spawn(stage, particle) {
    if (stage.live.length >= MAX) {
      return;
    }

    stage.live.push(particle);
  }

  function pick(list) {
    return list[Math.floor(Math.random() * list.length)];
  }

  /** A hit: fast, hot, short-lived, thrown out in every direction from one point. */
  function sparks(stage, size, at) {
    const tint = [colour("--po-accent", "#ffcc33"), colour("--po-danger", "#ff5a5a"), "#ffffff"];
    for (let i = 0; i < 90; i++) {
      const angle = Math.random() * Math.PI * 2;
      const speed = (120 + Math.random() * 620) * size.dpr;
      spawn(stage, {
        x: at.x, y: at.y,
        vx: Math.cos(angle) * speed,
        vy: Math.sin(angle) * speed,
        life: 0.35 + Math.random() * 0.45,
        age: 0,
        size: (1 + Math.random() * 2.5) * size.dpr,
        spin: 0, angle: 0,
        drag: 2.4,
        gravity: 0.45,
        colour: pick(tint),
        shape: "spark",
      });
    }
  }

  /** A win: slow, broad, tumbling, falling in from above the top edge. */
  function confetti(stage, size) {
    const tint = [
      colour("--po-accent", "#ffcc33"),
      colour("--po-p1", "#3fa9f5"),
      colour("--po-p2", "#ff4f8b"),
      colour("--po-success", "#4ade80"),
    ];
    for (let i = 0; i < 160; i++) {
      spawn(stage, {
        x: Math.random() * size.width,
        y: -Math.random() * size.height * 0.4,
        vx: (Math.random() - 0.5) * 160 * size.dpr,
        vy: (60 + Math.random() * 180) * size.dpr,
        life: 2.2 + Math.random() * 1.6,
        age: 0,
        size: (3 + Math.random() * 4) * size.dpr,
        spin: (Math.random() - 0.5) * 14,
        angle: Math.random() * Math.PI,
        drag: 1.1,
        gravity: 0.35,
        colour: pick(tint),
        shape: "flake",
      });
    }
  }

  /**
   * A card breaking: shards of it thrown up and away from where it was struck — the same point the crack in
   * PlayerCard's CSS radiates from, 70% across and 30% down — spinning as they fall. Each shard is its own triangle.
   */
  function shatter(stage, size, rect) {
    const tint = [colour("--po-surface-2", "#1c2230"), colour("--po-border-strong", "#525c73"), colour("--po-danger", "#ff5a5a"), "#ffffff"];
    const hitX = rect.x + rect.w * 0.7;
    const hitY = rect.y + rect.h * 0.3;
    // The cracks first: jagged lines run out from the point of impact across the card, and hold for the moment
    // before it gives. Each is a polyline that wanders a little off its heading, and stops at the card's edge.
    for (let i = 0; i < 9; i++) {
      const heading = (i / 9) * Math.PI * 2 + Math.random() * 0.5;
      const points = [hitX, hitY];
      let x = hitX;
      let y = hitY;
      while (x > rect.x && x < rect.x + rect.w && y > rect.y && y < rect.y + rect.h) {
        const a = heading + (Math.random() - 0.5) * 0.9;
        x += Math.cos(a) * 12 * size.dpr;
        y += Math.sin(a) * 12 * size.dpr;
        points.push(Math.min(Math.max(x, rect.x), rect.x + rect.w), Math.min(Math.max(y, rect.y), rect.y + rect.h));
      }

      spawn(stage, {
        x: 0, y: 0, vx: 0, vy: 0, life: 0.34, age: 0, size: 1.5 * size.dpr,
        spin: 0, angle: 0, drag: 0, gravity: 0, colour: "#ffffff", shape: "crack", corners: points,
      });
    }

    for (let i = 0; i < 70; i++) {
      const x = rect.x + Math.random() * rect.w;
      const y = rect.y + Math.random() * rect.h;
      const angle = Math.atan2(y - hitY, x - hitX) + (Math.random() - 0.5) * 0.6;
      const speed = (150 + Math.random() * 450) * size.dpr;
      const radius = (4 + Math.random() * 12) * size.dpr;
      const corners = [];
      for (let k = 0; k < 3; k++) {
        const a = k * 2.1 + (Math.random() - 0.5) * 0.9;
        const r = radius * (0.6 + Math.random() * 0.6);
        corners.push(Math.cos(a) * r, Math.sin(a) * r);
      }

      spawn(stage, {
        x: x, y: y,
        vx: Math.cos(angle) * speed,
        vy: Math.sin(angle) * speed - 220 * size.dpr,
        // A negative age is a delay: the shards wait out the cracks, then go. Long enough to settle on the floor.
        life: 2 + Math.random(),
        age: -0.3,
        size: radius,
        spin: (Math.random() - 0.5) * 16,
        angle: Math.random() * Math.PI * 2,
        drag: 0.8,
        gravity: 1,
        bounce: 0.35,
        colour: pick(tint),
        shape: "shard",
        corners: corners,
      });
    }
  }

  /**
   * A win, in ribbons: streamers that flutter down rather than tumble. Each is a chain of points stepped by Verlet
   * integration — a point's velocity is simply where it is minus where it was — with every link pulled back to its
   * length a few times a frame, so the ribbon bends and trails and cannot stretch. A breeze that changes with height
   * and time is what makes them flutter instead of fall.
   */
  function streamers(stage, size) {
    const tint = [
      colour("--po-accent", "#ffcc33"),
      colour("--po-p1", "#3fa9f5"),
      colour("--po-p2", "#ff4f8b"),
      colour("--po-success", "#4ade80"),
    ];
    const links = 9;
    for (let i = 0; i < 26; i++) {
      const x = Math.random() * size.width;
      const y = -Math.random() * size.height * 0.3;
      const link = (7 + Math.random() * 6) * size.dpr;
      const drift = (Math.random() - 0.5) * 3 * size.dpr;
      const points = [];
      for (let k = 0; k < links; k++) {
        // Hanging straight up from the head, already moving: the previous position is a little behind the current.
        points.push({ x: x, y: y - k * link, px: x - drift, py: y - k * link - 4 * size.dpr });
      }

      spawn(stage, {
        x: x, y: y, vx: 0, vy: 0,
        life: 3.5 + Math.random() * 1.5,
        age: 0,
        size: (2.5 + Math.random() * 2.5) * size.dpr,
        spin: 0, angle: 0, drag: 0, gravity: 0,
        colour: pick(tint),
        shape: "ribbon",
        points: points,
        link: link,
        phase: Math.random() * Math.PI * 2,
      });
    }
  }

  /** One Verlet step of a ribbon, and its links held to length. */
  function stepRibbon(p, seconds, size) {
    const gravity = 260 * size.dpr * seconds * seconds;
    p.points.forEach(function (point, k) {
      const breeze = Math.sin(p.age * 2.6 + p.phase + k * 0.18) * 140 * size.dpr * seconds * seconds;
      const vx = (point.x - point.px) * 0.985;
      const vy = (point.y - point.py) * 0.985;
      point.px = point.x;
      point.py = point.y;
      point.x += vx + breeze;
      point.y += vy + gravity;
    });

    for (let pass = 0; pass < 6; pass++) {
      for (let k = 1; k < p.points.length; k++) {
        const a = p.points[k - 1];
        const b = p.points[k];
        const dx = b.x - a.x;
        const dy = b.y - a.y;
        const length = Math.sqrt(dx * dx + dy * dy) || 0.0001;
        const pull = (length - p.link) / length / 2;
        a.x += dx * pull;
        a.y += dy * pull;
        b.x -= dx * pull;
        b.y -= dy * pull;
      }
    }
  }

  /** One ember, drifting up from the bottom edge. Emitted continuously rather than in a burst. */
  function ember(stage, size) {
    spawn(stage, {
      x: Math.random() * size.width,
      y: size.height + 4,
      vx: (Math.random() - 0.5) * 40 * size.dpr,
      vy: -(20 + Math.random() * 60) * size.dpr,
      life: 1.6 + Math.random() * 2.2,
      age: 0,
      size: (1 + Math.random() * 2) * size.dpr,
      spin: 0, angle: 0,
      drag: 0.6,
      gravity: -0.06,
      colour: colour("--po-danger", "#ff5a5a"),
      shape: "spark",
    });
  }

  function step(stage, seconds, size) {
    const ctx = stage.ctx;
    ctx.clearRect(0, 0, size.width, size.height);

    if (stage.emitting > 0) {
      // Carried between frames so a low rate still emits: a fractional particle a frame would otherwise be none.
      stage.emitCarry += stage.emitting * seconds;
      while (stage.emitCarry >= 1) {
        stage.emitCarry -= 1;
        ember(stage, size);
      }
    }

    const alive = [];
    for (let i = 0; i < stage.live.length; i++) {
      const p = stage.live[i];
      p.age += seconds;
      if (p.age >= p.life) {
        continue;
      }

      if (p.age < 0) {
        alive.push(p);
        continue;
      }

      if (p.shape === "ribbon") {
        stepRibbon(p, seconds, size);
        ctx.globalAlpha = Math.min(1, (1 - p.age / p.life) * 3);
        ctx.strokeStyle = p.colour;
        ctx.lineWidth = p.size;
        ctx.lineJoin = "round";
        ctx.lineCap = "round";
        // Through the midpoints, with each point as the control: a ribbon, not a chain of straight links.
        ctx.beginPath();
        ctx.moveTo(p.points[0].x, p.points[0].y);
        for (let k = 1; k < p.points.length - 1; k++) {
          const next = p.points[k + 1];
          ctx.quadraticCurveTo(p.points[k].x, p.points[k].y, (p.points[k].x + next.x) / 2, (p.points[k].y + next.y) / 2);
        }

        const tail = p.points[p.points.length - 1];
        ctx.lineTo(tail.x, tail.y);
        ctx.stroke();
        alive.push(p);
        continue;
      }

      const drag = Math.max(0, 1 - p.drag * seconds);
      p.vx *= drag;
      p.vy = p.vy * drag + GRAVITY * p.gravity * seconds * size.dpr;
      p.x += p.vx * seconds;
      p.y += p.vy * seconds;
      p.angle += p.spin * seconds;

      // The floor: whatever bounces lands on the bottom edge, loses most of its speed, and comes to rest there.
      if (p.bounce && p.y > size.height - p.size * 0.5) {
        p.y = size.height - p.size * 0.5;
        p.vy = Math.abs(p.vy) < 60 * size.dpr ? 0 : -p.vy * p.bounce;
        p.vx *= 0.7;
        p.spin *= 0.6;
      }

      // Fading out over the last third rather than the whole life: a particle that starts dimming immediately
      // reads as a rendering fault rather than a spark.
      const remaining = 1 - p.age / p.life;
      ctx.globalAlpha = Math.min(1, remaining * 3);
      ctx.fillStyle = p.colour;
      if (p.shape === "flake") {
        ctx.save();
        ctx.translate(p.x, p.y);
        ctx.rotate(p.angle);
        ctx.fillRect(-p.size, -p.size * 0.4, p.size * 2, p.size * 0.8);
        ctx.restore();
      } else if (p.shape === "crack") {
        ctx.strokeStyle = p.colour;
        ctx.lineWidth = p.size;
        ctx.beginPath();
        ctx.moveTo(p.corners[0], p.corners[1]);
        for (let k = 2; k < p.corners.length; k += 2) {
          ctx.lineTo(p.corners[k], p.corners[k + 1]);
        }

        ctx.stroke();
      } else if (p.shape === "shard") {
        ctx.save();
        ctx.translate(p.x, p.y);
        ctx.rotate(p.angle);
        ctx.beginPath();
        ctx.moveTo(p.corners[0], p.corners[1]);
        ctx.lineTo(p.corners[2], p.corners[3]);
        ctx.lineTo(p.corners[4], p.corners[5]);
        ctx.closePath();
        ctx.fill();
        // A bright edge on one side, which is all it takes for a flat triangle to read as broken glass.
        ctx.strokeStyle = "rgba(255,255,255,0.35)";
        ctx.lineWidth = 1;
        ctx.stroke();
        ctx.restore();
      } else {
        ctx.beginPath();
        ctx.arc(p.x, p.y, p.size, 0, Math.PI * 2);
        ctx.fill();
      }

      alive.push(p);
    }

    ctx.globalAlpha = 1;
    stage.live = alive;
  }

  function draw(now) {
    frame = 0;
    const seconds = Math.min(0.05, (now - last) / 1000 || 0.016);
    last = now;

    let working = false;
    stages.forEach(function (stage, selector) {
      const size = resize(stage);
      if (!document.hidden) {
        step(stage, seconds, size);
      }

      if (stage.live.length > 0 || stage.emitting > 0) {
        working = true;
      } else {
        // Nothing left and nothing coming: the canvas goes, and with it the reason to have a frame loop.
        stage.ctx.clearRect(0, 0, size.width, size.height);
        if (stage.canvas.parentNode) {
          stage.canvas.parentNode.removeChild(stage.canvas);
        }

        stages.delete(selector);
      }
    });

    if (working) {
      frame = requestAnimationFrame(draw);
    }
  }

  function pump() {
    if (!frame && stages.size > 0) {
      last = performance.now();
      frame = requestAnimationFrame(draw);
    }
  }

  /** Where in the canvas a child element sits, in device pixels. Measured once, when something happens. */
  function pointAt(stage, size, childSelector) {
    const child = childSelector ? stage.host.querySelector(childSelector) : null;
    if (!child) {
      return { x: size.width / 2, y: size.height / 2 };
    }

    const host = stage.host.getBoundingClientRect();
    const rect = child.getBoundingClientRect();
    return {
      x: (rect.left + rect.width / 2 - host.left) * size.dpr,
      y: (rect.top + rect.height / 2 - host.top) * size.dpr,
    };
  }

  /** The box a child element occupies, in device pixels. The whole canvas when there is no such child. */
  function rectAt(stage, size, childSelector) {
    const child = childSelector ? stage.host.querySelector(childSelector) : null;
    if (!child) {
      return { x: 0, y: 0, w: size.width, h: size.height };
    }

    const host = stage.host.getBoundingClientRect();
    const rect = child.getBoundingClientRect();
    return {
      x: (rect.left - host.left) * size.dpr,
      y: (rect.top - host.top) * size.dpr,
      w: rect.width * size.dpr,
      h: rect.height * size.dpr,
    };
  }

  return {
    /**
     * Throws a burst of `kind` into the element at `selector`, optionally centred on a child of it. Silent about
     * everything that can go wrong: no element, no canvas, no motion wanted — all of it is simply nothing.
     */
    burst: function (selector, kind, childSelector) {
      if (reducedMotion()) {
        return false;
      }

      try {
        const stage = stageFor(selector);
        if (!stage) {
          return false;
        }

        const size = resize(stage);
        if (kind === "confetti") {
          confetti(stage, size);
        } else if (kind === "sparks") {
          sparks(stage, size, pointAt(stage, size, childSelector));
        } else if (kind === "shatter") {
          shatter(stage, size, rectAt(stage, size, childSelector));
        } else if (kind === "streamers") {
          streamers(stage, size);
        } else {
          return false;
        }

        pump();
        return true;
      } catch (e) {
        return false;
      }
    },

    /** Embers, per second. Zero stops them; the canvas leaves of its own accord once the last one dies. */
    emit: function (selector, perSecond) {
      if (reducedMotion()) {
        return;
      }

      try {
        const rate = typeof perSecond === "number" && perSecond > 0 ? Math.min(perSecond, 60) : 0;
        if (rate === 0) {
          const existing = stages.get(selector);
          if (existing) {
            existing.emitting = 0;
          }

          return;
        }

        const stage = stageFor(selector);
        if (stage) {
          stage.emitting = rate;
          pump();
        }
      } catch (e) {
        // Nothing to emit into.
      }
    },

    /** Everything stops and the canvas goes, now rather than when the last particle dies. */
    clear: function (selector) {
      const stage = stages.get(selector);
      if (!stage) {
        return;
      }

      stages.delete(selector);
      stage.emitting = 0;
      stage.live = [];
      if (stage.canvas.parentNode) {
        stage.canvas.parentNode.removeChild(stage.canvas);
      }

      if (stages.size === 0 && frame) {
        cancelAnimationFrame(frame);
        frame = 0;
      }
    },
  };
})();
