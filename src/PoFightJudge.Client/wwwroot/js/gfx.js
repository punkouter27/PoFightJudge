// PoGfx — the drawn layer. One WebGL2 canvas per mounted element, a shared frame loop, and nothing that any page
// depends on: every entry point answers false rather than throwing, and a browser with no WebGL2 simply gets the
// CSS the markup already had. Mounted from .NET; drawn entirely here, because sixty frames a second is not a
// conversation to have across the interop boundary.
//
// Colours come from the design tokens, but they cannot be read as text: every token is one light-dark() pair, and
// getPropertyValue hands back the declaration rather than the colour this theme resolved it to. So a hidden probe
// element is given `color: var(--po-p1)` and its computed colour is read back — the browser does the resolving,
// and the same trick re-runs when the theme is switched underneath us.
//
// Reduced motion turns the whole thing off. It is the app's one motion switch and this is the most motion in it.
window.PoGfx = (function () {
  "use strict";

  /** Retina is not worth the fill rate for a soft glow; 1.5× is past the point anybody can see the difference. */
  const MAX_DPR = 1.5;

  /** Two triangles covering the canvas: every pass of every shader here is drawn on this. */
  const QUAD = new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]);

  const VERTEX = `#version 300 es
in vec2 a_position;
out vec2 v_uv;
void main() {
  v_uv = a_position * 0.5 + 0.5;
  gl_Position = vec4(a_position, 0.0, 1.0);
}`;

  // Value noise and a four-octave fbm, which is what keeps an aura from looking like a radial gradient with the
  // opacity turned down. Cheap enough that the whole thing runs on a phone's integrated GPU.
  const NOISE = `
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123); }
float noise(vec2 p) {
  vec2 i = floor(p);
  vec2 f = fract(p);
  vec2 u = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
             mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}
float fbm(vec2 p) {
  float sum = 0.0;
  float amp = 0.5;
  for (int i = 0; i < 4; i++) { sum += amp * noise(p); p *= 2.03; amp *= 0.5; }
  return sum;
}`;

  // The watch stage: an aura under whoever is speaking, breathing with their voice, and the ring a slap sends out.
  const STAGE = `#version 300 es
precision highp float;
in vec2 v_uv;
out vec4 outColour;
uniform vec2 u_res;
uniform vec2 u_focus;
uniform float u_time;
uniform float u_level;
uniform float u_side;
uniform float u_shock;
uniform vec3 u_c1;
uniform vec3 u_c2;
${NOISE}
void main() {
  float aspect = u_res.x / max(u_res.y, 1.0);
  vec2 centre = vec2(0.5, 0.5);
  vec2 p = vec2((v_uv.x - u_focus.x) * aspect, v_uv.y - u_focus.y);
  float d = length(p);
  vec3 tint = mix(u_c1, u_c2, clamp(u_side * 0.5 + 0.5, 0.0, 1.0));

  float n = fbm(p * 3.0 + vec2(u_time * 0.13, u_time * 0.09));
  float reach = 0.16 + u_level * 0.5;
  float aura = smoothstep(reach + 0.28 * n, 0.0, d) * (0.2 + u_level * 0.8);
  vec3 colour = tint * aura;
  float alpha = aura * 0.6;

  if (u_shock >= 0.0) {
    float life = clamp(1.0 - u_shock / 0.7, 0.0, 1.0);
    vec2 q = vec2((v_uv.x - centre.x) * aspect, v_uv.y - centre.y);
    float r = length(q);
    float radius = u_shock * 1.7;
    // Three rings a hair apart: the channels pull away from each other at the edge of the shock.
    vec3 split = vec3(
      smoothstep(0.05, 0.0, abs(r - radius * 1.04)),
      smoothstep(0.05, 0.0, abs(r - radius)),
      smoothstep(0.05, 0.0, abs(r - radius * 0.96)));
    colour += split * life * 1.5;
    float flash = life * life * 0.4 * smoothstep(0.6, 0.0, r);
    colour += vec3(flash);
    alpha = max(alpha, max(max(split.r, split.g), max(split.b, flash)) * life);
  }

  outColour = vec4(colour, clamp(alpha, 0.0, 1.0));
}`;

  // The live fight's backdrop: a slow flowing field between the two fighters' colours, biased toward whoever has
  // the floor and heating up as the clock runs down. It sits behind the cards, so most of what shows is the gaps.
  const BACKDROP = `#version 300 es
precision highp float;
in vec2 v_uv;
out vec4 outColour;
uniform vec2 u_res;
uniform float u_time;
uniform float u_level;
uniform float u_mix;
uniform float u_heat;
uniform vec3 u_c1;
uniform vec3 u_c2;
uniform vec3 u_c3;
${NOISE}
vec3 paint(float field) {
  float pull = (clamp(u_mix, 0.0, 1.0) - 0.5) * 0.8;
  vec3 colour = mix(u_c1, u_c2, clamp(v_uv.x * 0.8 + field * 0.5 - 0.15 - pull, 0.0, 1.0));
  return mix(colour, u_c3, u_heat * 0.5 * (0.35 + 0.65 * field));
}

void main() {
  float aspect = u_res.x / max(u_res.y, 1.0);
  vec2 p = vec2(v_uv.x * aspect, v_uv.y);
  float t = u_time * (0.05 + u_heat * 0.2);

  // Heat haze: the hotter it runs, the more the field is seen through a shimmer rising off the floor — stronger
  // low down, where the heat would be coming from.
  p.x += sin(p.y * 38.0 - u_time * 6.0) * 0.008 * u_heat * (1.2 - v_uv.y);

  // Warping the domain before sampling it again is what turns four octaves of value noise into something that
  // looks like it is moving rather than scrolling.
  vec2 warp = vec2(fbm(p * 1.5 + t), fbm(p * 1.5 + 4.7 - t));
  vec2 flow = warp * 1.3 + vec2(0.0, t * 0.6);
  float field = fbm(p * 2.1 + flow);
  vec3 colour = paint(field);

  // Chromatic aberration at the death: red and blue are read a little apart from green, further toward the edges,
  // the way a lens that is being pushed too hard splits light. Only paid for once there is heat to show.
  if (u_heat > 0.0) {
    vec2 split = (v_uv - 0.5) * vec2(aspect, 1.0) * 0.06 * u_heat;
    colour.r = paint(fbm((p + split) * 2.1 + flow)).r;
    colour.b = paint(fbm((p - split) * 2.1 + flow)).b;
  }

  float vignette = smoothstep(1.2, 0.2, length((v_uv - 0.5) * vec2(aspect, 1.0)));
  float alpha = (0.14 + 0.2 * field + u_heat * 0.13 + u_level * 0.08) * vignette;
  outColour = vec4(colour, clamp(alpha, 0.0, 1.0));
}`;

  // The verdict, finished like a piece of film: a vignette that pulls the eye in and grain over the top of it.
  // This one is mounted OVER the page rather than under it, which is the only way grain reads as grain.
  const GRAIN = `#version 300 es
precision highp float;
in vec2 v_uv;
out vec4 outColour;
uniform vec2 u_res;
uniform vec2 u_focus;
uniform float u_time;
uniform float u_shock;
uniform vec3 u_c3;
${NOISE}
void main() {
  float aspect = u_res.x / max(u_res.y, 1.0);
  float vignette = smoothstep(0.45, 1.15, length((v_uv - 0.5) * vec2(aspect, 1.0)));

  // Sampled at pixel scale and moved every frame, because grain that holds still is a texture, not grain.
  float grain = hash(v_uv * u_res + fract(u_time) * 431.0) - 0.5;
  float alpha = vignette * 0.3 + abs(grain) * 0.05;
  vec3 colour = mix(vec3(0.0), u_c3 * 0.35, vignette * 0.25) + vec3(grain * 0.12);

  // The gavel: a ring out from the winner's name, measured in widths so it travels at the same speed down a long
  // report as a short one, its three channels a hair apart at the edge, and a flash where it started.
  if (u_shock >= 0.0) {
    float life = clamp(1.0 - u_shock / 0.8, 0.0, 1.0);
    vec2 q = (v_uv - u_focus) * u_res / max(u_res.x, 1.0);
    float r = length(q);
    float radius = u_shock * 1.4;
    vec3 split = vec3(
      smoothstep(0.03, 0.0, abs(r - radius * 1.05)),
      smoothstep(0.03, 0.0, abs(r - radius)),
      smoothstep(0.03, 0.0, abs(r - radius * 0.95)));
    float flash = life * life * 0.4 * smoothstep(0.35, 0.0, r);
    colour += split * life * 1.3 + vec3(flash);
    alpha = max(alpha, max(max(split.r, split.g), max(split.b, flash)) * life * 0.9);
  }

  outColour = vec4(colour, clamp(alpha, 0.0, 0.9));
}`;

  // The winner's light: shafts breaking out from behind their card, turning slowly, bright on the moment and settling
  // into a glow that stays while the page is up. The shafts are noise sampled around a circle rather than along the
  // angle, so there is no seam where the angle wraps; two layers turn opposite ways at different rates, which is what
  // makes them shimmer instead of rotate. Mounted under the page, so the card itself blocks its own light and the
  // shafts are seen breaking out around it.
  const RAYS = `#version 300 es
precision highp float;
in vec2 v_uv;
out vec4 outColour;
uniform vec2 u_res;
uniform vec2 u_focus;
uniform float u_time;
uniform float u_side;
uniform vec3 u_c1;
uniform vec3 u_c2;
uniform vec3 u_c3;
${NOISE}
vec2 turned(float a) { return vec2(cos(a), sin(a)); }
void main() {
  vec2 q = (v_uv - u_focus) * u_res / max(u_res.y, 1.0);
  float r = length(q);
  float a = atan(q.y, q.x);
  float t = u_time;

  float wide = pow(noise(turned(a + t * 0.08) * 9.0 + 3.1), 4.0) * 1.2;
  float thin = pow(noise(turned(a - t * 0.05) * 17.0 - 1.7), 6.0);
  float burst = smoothstep(0.0, 0.35, t) * (0.5 + 0.5 * exp(-t * 0.7));
  float reach = 0.25 + burst * 1.4;
  float light = ((wide + thin) * smoothstep(reach, 0.0, r) + smoothstep(0.4, 0.0, r) * 0.6) * burst;

  vec3 tint = mix(u_c3, u_side < 0.0 ? u_c1 : u_c2, 0.35);
  outColour = vec4(tint, clamp(light * 0.8, 0.0, 0.75));
}`;

  // ── The home page's two swarms ──
  // Every particle is stepped on the GPU: a vertex shader reads one particle's position and velocity, steers it, and
  // transform feedback writes the result into the other buffer of a pair — no pixel is drawn on that pass. Each of
  // the two flocks is pulled toward a home of its own, wandering a Lissajous path on its side of the screen, and
  // shoved off the other flock's home when it gets close; curl noise (the curl of a value-noise field, so it swirls
  // without bunching) gives them somewhere to go in between. Every so often both homes drift into the middle and the
  // flocks meet head on. Then they are drawn as soft points, brighter the faster they move, added on top of each
  // other so a crowd glows. Space is the height's units: x runs ±aspect, y runs ±1.
  const SWARM_STEP = `#version 300 es
precision highp float;
in vec2 a_pos;
in vec2 a_vel;
in float a_flock;
out vec2 v_pos;
out vec2 v_vel;
out float v_flock;
uniform float u_time;
uniform float u_dt;
uniform vec2 u_home[2];
uniform float u_aspect;
${NOISE}
void main() {
  bool first = a_flock < 0.5;
  vec2 p = a_pos;
  vec2 v = a_vel;
  vec2 home = first ? u_home[0] : u_home[1];
  vec2 rival = first ? u_home[1] : u_home[0];

  vec2 toHome = home - p;
  float d = length(toHome);
  vec2 dir = toHome / max(d, 0.001);
  vec2 steer = dir * min(d * 1.5, 1.0) * 1.2;
  // Round and round their home rather than straight at it — a murmuration, not a queue — the two turning opposite
  // ways, so where they meet they shear.
  steer += vec2(-dir.y, dir.x) * (first ? 1.0 : -1.0) * 1.0 * smoothstep(0.0, 0.35, d);
  vec2 away = p - rival;
  float r = length(away);
  steer += away / max(r, 0.001) * smoothstep(0.45, 0.0, r) * 1.4;

  float e = 0.02;
  vec2 q = p * 2.2 + vec2(u_time * 0.06, first ? 0.0 : 9.0);
  vec2 curl = vec2(noise(q + vec2(0.0, e)) - noise(q - vec2(0.0, e)), noise(q - vec2(e, 0.0)) - noise(q + vec2(e, 0.0))) / (2.0 * e);

  v += (steer + curl * 0.35) * u_dt;
  v *= 1.0 - 1.1 * u_dt;
  float speed = length(v);
  if (speed > 0.9) { v *= 0.9 / speed; }
  p += v * u_dt;
  p = clamp(p, vec2(-u_aspect * 1.1, -1.1), vec2(u_aspect * 1.1, 1.1));

  v_pos = p;
  v_vel = v;
  v_flock = a_flock;
  gl_Position = vec4(0.0);
}`;

  const SWARM_NOTHING = `#version 300 es
precision mediump float;
out vec4 outColour;
void main() { outColour = vec4(0.0); }`;

  const SWARM_POINTS = `#version 300 es
precision highp float;
in vec2 a_pos;
in vec2 a_vel;
in float a_flock;
out vec3 v_colour;
out float v_glow;
uniform float u_aspect;
uniform float u_size;
uniform vec3 u_c1;
uniform vec3 u_c2;
void main() {
  gl_Position = vec4(a_pos.x / u_aspect, a_pos.y, 0.0, 1.0);
  float speed = length(a_vel);
  gl_PointSize = u_size * (0.8 + speed * 2.2);
  v_colour = a_flock < 0.5 ? u_c1 : u_c2;
  v_glow = 0.3 + speed * 1.6;
}`;

  const SWARM = `#version 300 es
precision highp float;
in vec3 v_colour;
in float v_glow;
out vec4 outColour;
void main() {
  // Dim each one: a crowd of them adds up to a glow, and a page's words are read over the top of it.
  float a = smoothstep(0.5, 0.0, length(gl_PointCoord - 0.5)) * v_glow * 0.32;
  outColour = vec4(v_colour * a, a);
}`;

  /** How many in each flock. Twelve thousand apiece is a crowd, and still nothing to a phone's GPU. */
  const SWARM_EACH = 12000;

  // ── The live fight's ink: a real fluid, not a picture of one ──
  // Stable fluids on the GPU (Stam, after Dobryakov's WebGL port): velocity, pressure and dye live in half-float
  // textures and are stepped every frame — vorticity confinement for the curl, a Jacobi solve to keep it
  // incompressible, and semi-Lagrangian advection to carry the ink. Whoever has the floor pours their colour in from
  // their side of the stage, as hard as they are talking; the host pours from the bottom. Every pass shares one
  // vertex shader that hands the fragment its four neighbours.
  const FLUID_VERTEX = `#version 300 es
in vec2 a_position;
out vec2 v_uv;
out vec2 v_l;
out vec2 v_r;
out vec2 v_t;
out vec2 v_b;
uniform vec2 u_texel;
void main() {
  v_uv = a_position * 0.5 + 0.5;
  v_l = v_uv - vec2(u_texel.x, 0.0);
  v_r = v_uv + vec2(u_texel.x, 0.0);
  v_t = v_uv + vec2(0.0, u_texel.y);
  v_b = v_uv - vec2(0.0, u_texel.y);
  gl_Position = vec4(a_position, 0.0, 1.0);
}`;

  const FLUID_HEAD = `#version 300 es
precision highp float;
precision highp sampler2D;
in vec2 v_uv;
in vec2 v_l;
in vec2 v_r;
in vec2 v_t;
in vec2 v_b;
out vec4 outColour;
`;

  const FLUID_PASSES = {
    // A gaussian of colour (or of momentum) added where somebody is pouring.
    splat: FLUID_HEAD + `
uniform sampler2D u_target;
uniform float u_aspect;
uniform vec3 u_colour;
uniform vec2 u_point;
uniform float u_radius;
void main() {
  vec2 p = v_uv - u_point;
  p.x *= u_aspect;
  vec3 splat = exp(-dot(p, p) / u_radius) * u_colour;
  outColour = vec4(texture(u_target, v_uv).xyz + splat, 1.0);
}`,
    // Carry a quantity along the velocity field by looking backwards along it, and let it fade.
    advect: FLUID_HEAD + `
uniform sampler2D u_velocity;
uniform sampler2D u_source;
uniform vec2 u_simTexel;
uniform float u_dt;
uniform float u_dissipation;
void main() {
  vec2 coord = v_uv - u_dt * texture(u_velocity, v_uv).xy * u_simTexel;
  outColour = texture(u_source, coord) / (1.0 + u_dissipation * u_dt);
}`,
    divergence: FLUID_HEAD + `
uniform sampler2D u_velocity;
void main() {
  float l = texture(u_velocity, v_l).x;
  float r = texture(u_velocity, v_r).x;
  float t = texture(u_velocity, v_t).y;
  float b = texture(u_velocity, v_b).y;
  vec2 c = texture(u_velocity, v_uv).xy;
  // The walls reflect: nothing flows out of the stage.
  if (v_l.x < 0.0) { l = -c.x; }
  if (v_r.x > 1.0) { r = -c.x; }
  if (v_t.y > 1.0) { t = -c.y; }
  if (v_b.y < 0.0) { b = -c.y; }
  outColour = vec4(0.5 * (r - l + t - b), 0.0, 0.0, 1.0);
}`,
    curl: FLUID_HEAD + `
uniform sampler2D u_velocity;
void main() {
  float l = texture(u_velocity, v_l).y;
  float r = texture(u_velocity, v_r).y;
  float t = texture(u_velocity, v_t).x;
  float b = texture(u_velocity, v_b).x;
  outColour = vec4(0.5 * (r - l - t + b), 0.0, 0.0, 1.0);
}`,
    // Vorticity confinement: the swirls numerical damping would smear out are pushed back in, so the ink curls.
    vorticity: FLUID_HEAD + `
uniform sampler2D u_velocity;
uniform sampler2D u_curl;
uniform float u_strength;
uniform float u_dt;
void main() {
  float l = texture(u_curl, v_l).x;
  float r = texture(u_curl, v_r).x;
  float t = texture(u_curl, v_t).x;
  float b = texture(u_curl, v_b).x;
  float c = texture(u_curl, v_uv).x;
  vec2 force = 0.5 * vec2(abs(t) - abs(b), abs(r) - abs(l));
  force /= length(force) + 0.0001;
  force *= u_strength * c;
  force.y *= -1.0;
  vec2 velocity = texture(u_velocity, v_uv).xy + force * u_dt;
  outColour = vec4(clamp(velocity, -1000.0, 1000.0), 0.0, 1.0);
}`,
    pressure: FLUID_HEAD + `
uniform sampler2D u_pressure;
uniform sampler2D u_divergence;
void main() {
  float l = texture(u_pressure, v_l).x;
  float r = texture(u_pressure, v_r).x;
  float t = texture(u_pressure, v_t).x;
  float b = texture(u_pressure, v_b).x;
  outColour = vec4((l + r + b + t - texture(u_divergence, v_uv).x) * 0.25, 0.0, 0.0, 1.0);
}`,
    // What is left once the pressure gradient is taken off is a field with no divergence: the fluid neither piles
    // up nor thins out, which is the whole difference between ink and smoke from a cheap particle system.
    gradient: FLUID_HEAD + `
uniform sampler2D u_pressure;
uniform sampler2D u_velocity;
void main() {
  float l = texture(u_pressure, v_l).x;
  float r = texture(u_pressure, v_r).x;
  float t = texture(u_pressure, v_t).x;
  float b = texture(u_pressure, v_b).x;
  outColour = vec4(texture(u_velocity, v_uv).xy - vec2(r - l, t - b), 0.0, 1.0);
}`,
    scale: FLUID_HEAD + `
uniform sampler2D u_source;
uniform float u_value;
void main() {
  outColour = u_value * texture(u_source, v_uv);
}`,
  };

  // The dye, shown. Tone-mapped so a pile-up of ink saturates instead of clipping, its alpha taken from how much ink
  // there is, run hot toward the danger colour as the clock runs down, and pulled in by a vignette like the rest.
  const FLUID = FLUID_HEAD + `
uniform sampler2D u_dye;
uniform vec2 u_res;
uniform float u_heat;
uniform vec3 u_c3;
void main() {
  float aspect = u_res.x / max(u_res.y, 1.0);
  vec3 ink = 1.0 - exp(-texture(u_dye, v_uv).rgb * 1.8);
  float amount = max(ink.r, max(ink.g, ink.b));
  vec3 colour = mix(ink / max(amount, 0.001), u_c3, u_heat * 0.45);
  float vignette = smoothstep(1.3, 0.25, length((v_uv - 0.5) * vec2(aspect, 1.0)));
  outColour = vec4(colour, clamp(amount * 0.9, 0.0, 0.85) * vignette);
}`;

  const SHADERS = { "stage": STAGE, "backdrop": BACKDROP, "grain": GRAIN, "fluid": FLUID, "rays": RAYS, "swarm": SWARM };

  /** The tuning, in one place. Units are the simulation's own: texels per second, per-second dissipation. */
  const FLUID_TUNING = {
    simResolution: 128,
    dyeResolution: 512,
    velocityDissipation: 0.25,
    dyeDissipation: 0.55,
    pressureDecay: 0.8,
    pressureIterations: 20,
    curl: 28,
    radius: 0.0035,
    force: 6500,
    ink: 2.2,
  };

  const mounts = new Map();
  let frame = 0;
  let probeEl = null;
  let watchingTheme = false;

  function reducedMotion() {
    try {
      return window.PoMotion.still();
    } catch (e) {
      return false;
    }
  }

  /** What this browser can actually do, for a page that wants to know before it asks for it. */
  function probe() {
    let webgl2 = false;
    try {
      webgl2 = !!document.createElement("canvas").getContext("webgl2");
    } catch (e) {
      webgl2 = false;
    }

    return {
      webgl2: webgl2,
      webgpu: !!(navigator && navigator.gpu),
      backdropFilter: supportsBackdrop(),
      reducedMotion: reducedMotion(),
    };
  }

  function supportsBackdrop() {
    try {
      return CSS.supports("backdrop-filter", "blur(2px)") || CSS.supports("-webkit-backdrop-filter", "blur(2px)");
    } catch (e) {
      return false;
    }
  }

  /**
   * A token's colour as this theme resolved it. The probe is given `color: var(--token)` and asked what colour it
   * ended up; light-dark() cannot be unpicked any other way from script.
   */
  function tokenColour(token, fallback) {
    try {
      if (!probeEl) {
        probeEl = document.createElement("span");
        probeEl.style.cssText = "position:absolute;left:-9999px;top:-9999px;width:0;height:0;";
        document.body.appendChild(probeEl);
      }

      probeEl.style.color = "var(" + token + ")";
      const computed = getComputedStyle(probeEl).color;
      const parts = computed.match(/[\d.]+/g);
      if (parts && parts.length >= 3) {
        return [parseFloat(parts[0]) / 255, parseFloat(parts[1]) / 255, parseFloat(parts[2]) / 255];
      }
    } catch (e) {
      // Fall through to the caller's colour.
    }

    return fallback;
  }

  function compile(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      gl.deleteShader(shader);
      return null;
    }

    return shader;
  }

  function program(gl, fragment) {
    const vs = compile(gl, gl.VERTEX_SHADER, VERTEX);
    const fs = compile(gl, gl.FRAGMENT_SHADER, fragment);
    if (!vs || !fs) {
      return null;
    }

    const prog = gl.createProgram();
    gl.attachShader(prog, vs);
    gl.attachShader(prog, fs);
    gl.linkProgram(prog);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
      gl.deleteProgram(prog);
      return null;
    }

    return prog;
  }

  // ── Fluid plumbing ──

  /** Compiled against the fluid's vertex shader, the quad pinned to attribute 0 so one vertex array serves every pass. */
  function fluidProgram(gl, fragment) {
    const vs = compile(gl, gl.VERTEX_SHADER, FLUID_VERTEX);
    const fs = compile(gl, gl.FRAGMENT_SHADER, fragment);
    if (!vs || !fs) {
      return null;
    }

    const prog = gl.createProgram();
    gl.attachShader(prog, vs);
    gl.attachShader(prog, fs);
    gl.bindAttribLocation(prog, 0, "a_position");
    gl.linkProgram(prog);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
      gl.deleteProgram(prog);
      return null;
    }

    // Every uniform the linker kept, by name. One the linker dropped is undefined, which WebGL treats as null: a
    // uniform call on it does nothing, which is the right answer for a value the pass never reads.
    const u = {};
    const count = gl.getProgramParameter(prog, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < count; i++) {
      const name = gl.getActiveUniform(prog, i).name;
      u[name] = gl.getUniformLocation(prog, name);
    }

    return { prog: prog, u: u };
  }

  /** One half-float texture and the framebuffer that draws into it, or null when this GPU will not draw into one. */
  function renderTarget(gl, w, h, internal, format) {
    const texture = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D, 0, internal, w, h, 0, format, gl.HALF_FLOAT, null);
    const fbo = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
    if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) {
      return null;
    }

    gl.viewport(0, 0, w, h);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    return { texture: texture, fbo: fbo, w: w, h: h };
  }

  /** Two targets: every pass reads one and writes the other, and they swap. A texture cannot be both at once. */
  function pingPong(gl, w, h, internal, format) {
    let read = renderTarget(gl, w, h, internal, format);
    let write = renderTarget(gl, w, h, internal, format);
    if (!read || !write) {
      return null;
    }

    return {
      w: w,
      h: h,
      get read() { return read; },
      get write() { return write; },
      swap: function () { const t = read; read = write; write = t; },
      all: function () { return [read, write]; },
    };
  }

  /** The short side at `resolution` texels and the long side in proportion, so a texel is square on screen. */
  function gridFor(canvas, resolution) {
    const ratio = canvas.width / Math.max(canvas.height, 1);
    const long = Math.round(resolution * (ratio >= 1 ? ratio : 1 / ratio));
    return ratio >= 1 ? { w: long, h: resolution } : { w: resolution, h: long };
  }

  function buildFluid(entry) {
    const gl = entry.gl;

    // Without float render targets there is nothing to simulate in. False, and the page mounts the backdrop.
    if (!gl.getExtension("EXT_color_buffer_float")) {
      return false;
    }

    const passes = {};
    for (const name in FLUID_PASSES) {
      passes[name] = fluidProgram(gl, FLUID_PASSES[name]);
      if (!passes[name]) {
        return false;
      }
    }

    const display = fluidProgram(gl, entry.source);
    if (!display) {
      return false;
    }

    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, QUAD, gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    gl.disable(gl.BLEND);

    entry.passes = passes;
    entry.display = display;
    entry.targets = [];
    entry.lastStep = 0;
    entry.nextWisp = 0;
    return allocate(entry);
  }

  /** Every texture the simulation steps, sized to the canvas as it is now. Run again when the canvas changes size. */
  function allocate(entry) {
    const gl = entry.gl;
    entry.targets.forEach(function (t) {
      gl.deleteTexture(t.texture);
      gl.deleteFramebuffer(t.fbo);
    });

    const sim = gridFor(entry.canvas, FLUID_TUNING.simResolution);
    const dye = gridFor(entry.canvas, FLUID_TUNING.dyeResolution);
    entry.velocity = pingPong(gl, sim.w, sim.h, gl.RG16F, gl.RG);
    entry.dye = pingPong(gl, dye.w, dye.h, gl.RGBA16F, gl.RGBA);
    entry.pressure = pingPong(gl, sim.w, sim.h, gl.R16F, gl.RED);
    entry.divergence = renderTarget(gl, sim.w, sim.h, gl.R16F, gl.RED);
    entry.curl = renderTarget(gl, sim.w, sim.h, gl.R16F, gl.RED);
    entry.grid = entry.canvas.width + "x" + entry.canvas.height;
    if (!entry.velocity || !entry.dye || !entry.pressure || !entry.divergence || !entry.curl) {
      entry.targets = [];
      return false;
    }

    entry.targets = entry.velocity.all().concat(entry.dye.all(), entry.pressure.all(), [entry.divergence, entry.curl]);
    return true;
  }

  function texture(gl, unit, target) {
    gl.activeTexture(gl.TEXTURE0 + unit);
    gl.bindTexture(gl.TEXTURE_2D, target.texture);
    return unit;
  }

  /** One pass: its program, its uniforms, drawn into `target` (or onto the canvas when there is none). */
  function run(entry, pass, target, set) {
    const gl = entry.gl;
    gl.useProgram(pass.prog);
    set(pass.u);
    gl.bindFramebuffer(gl.FRAMEBUFFER, target ? target.fbo : null);
    gl.viewport(0, 0, target ? target.w : entry.canvas.width, target ? target.h : entry.canvas.height);
    gl.drawArrays(gl.TRIANGLES, 0, 6);
  }

  /** Momentum (dx, dy) and colour poured in at one point, in the 0–1 space of the stage. */
  function splat(entry, x, y, dx, dy, colour) {
    const gl = entry.gl;
    const ratio = entry.canvas.width / Math.max(entry.canvas.height, 1);
    const radius = FLUID_TUNING.radius * Math.max(ratio, 1);
    [[entry.velocity, [dx, dy, 0]], [entry.dye, colour]].forEach(function (job) {
      const field = job[0];
      run(entry, entry.passes.splat, field.write, function (u) {
        gl.uniform2f(u.u_texel, 1 / field.w, 1 / field.h);
        gl.uniform1i(u.u_target, texture(gl, 0, field.read));
        gl.uniform1f(u.u_aspect, ratio);
        gl.uniform2f(u.u_point, x, y);
        gl.uniform3f(u.u_colour, job[1][0], job[1][1], job[1][2]);
        gl.uniform1f(u.u_radius, radius);
      });
      field.swap();
    });
  }

  function blend(a, b, t) {
    return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
  }

  /**
   * Who is pouring, and how hard. A fighter with the floor pours from their own side toward the middle; nobody with
   * the floor means the host, pouring up from the bottom between them. The ink is that side's colour, warmed toward
   * the danger colour by the heat — and a wisp from one side or the other every couple of seconds, so a silent room
   * still has something moving in it.
   */
  function pour(entry, now, dt) {
    const side = entry.mix < 0.4 ? -1 : entry.mix > 0.6 ? 1 : 0;
    const level = entry.smoothed;
    const t = now / 1000;
    if (level > 0.03) {
      const sway = Math.sin(t * 1.3 + side) * 0.18;
      const x = side < 0 ? 0.05 : side > 0 ? 0.95 : 0.5 + sway * 0.6;
      const y = side === 0 ? 0.04 : 0.45 + sway;
      const dx = side === 0 ? sway * 0.8 : -side;
      const dy = side === 0 ? 1 : Math.cos(t * 1.7) * 0.35;
      const force = FLUID_TUNING.force * level * dt;
      const base = side < 0 ? entry.c1 : side > 0 ? entry.c2 : blend(entry.c1, entry.c2, 0.5);
      const ink = FLUID_TUNING.ink * level * dt * 4;
      const warm = blend(base, entry.c3, entry.heat * 0.5);
      splat(entry, x, y, dx * force, dy * force, [warm[0] * ink, warm[1] * ink, warm[2] * ink]);
    }

    if (now > entry.nextWisp) {
      entry.nextWisp = now + 1800 + Math.random() * 1600;
      const left = Math.random() < 0.5;
      const colour = left ? entry.c1 : entry.c2;
      splat(entry, left ? 0.08 : 0.92, 0.2 + Math.random() * 0.6, (left ? 1 : -1) * 240, (Math.random() - 0.5) * 160,
        [colour[0] * 0.6, colour[1] * 0.6, colour[2] * 0.6]);
    }
  }

  /** One step of the fluid: curl, confinement, divergence, pressure, projection, then carry velocity and ink along. */
  function simulate(entry, dt) {
    const gl = entry.gl;
    const P = entry.passes;
    const v = entry.velocity;
    const pressure = entry.pressure;
    const sx = 1 / v.w;
    const sy = 1 / v.h;

    run(entry, P.curl, entry.curl, function (u) {
      gl.uniform2f(u.u_texel, sx, sy);
      gl.uniform1i(u.u_velocity, texture(gl, 0, v.read));
    });
    run(entry, P.vorticity, v.write, function (u) {
      gl.uniform2f(u.u_texel, sx, sy);
      gl.uniform1i(u.u_velocity, texture(gl, 0, v.read));
      gl.uniform1i(u.u_curl, texture(gl, 1, entry.curl));
      // More curl as the clock runs down: the last half-minute churns.
      gl.uniform1f(u.u_strength, FLUID_TUNING.curl * (1 + entry.heat));
      gl.uniform1f(u.u_dt, dt);
    });
    v.swap();

    run(entry, P.divergence, entry.divergence, function (u) {
      gl.uniform2f(u.u_texel, sx, sy);
      gl.uniform1i(u.u_velocity, texture(gl, 0, v.read));
    });

    // Last frame's pressure, decayed, is a far better first guess for the solve than zero.
    run(entry, P.scale, pressure.write, function (u) {
      gl.uniform2f(u.u_texel, sx, sy);
      gl.uniform1i(u.u_source, texture(gl, 0, pressure.read));
      gl.uniform1f(u.u_value, FLUID_TUNING.pressureDecay);
    });
    pressure.swap();
    for (let i = 0; i < FLUID_TUNING.pressureIterations; i++) {
      run(entry, P.pressure, pressure.write, function (u) {
        gl.uniform2f(u.u_texel, sx, sy);
        gl.uniform1i(u.u_pressure, texture(gl, 0, pressure.read));
        gl.uniform1i(u.u_divergence, texture(gl, 1, entry.divergence));
      });
      pressure.swap();
    }

    run(entry, P.gradient, v.write, function (u) {
      gl.uniform2f(u.u_texel, sx, sy);
      gl.uniform1i(u.u_pressure, texture(gl, 0, pressure.read));
      gl.uniform1i(u.u_velocity, texture(gl, 1, v.read));
    });
    v.swap();

    run(entry, P.advect, v.write, function (u) {
      gl.uniform2f(u.u_texel, sx, sy);
      gl.uniform2f(u.u_simTexel, sx, sy);
      gl.uniform1i(u.u_velocity, texture(gl, 0, v.read));
      gl.uniform1i(u.u_source, 0);
      gl.uniform1f(u.u_dt, dt);
      gl.uniform1f(u.u_dissipation, FLUID_TUNING.velocityDissipation);
    });
    v.swap();

    const dye = entry.dye;
    run(entry, P.advect, dye.write, function (u) {
      gl.uniform2f(u.u_texel, 1 / dye.w, 1 / dye.h);
      gl.uniform2f(u.u_simTexel, sx, sy);
      gl.uniform1i(u.u_velocity, texture(gl, 0, v.read));
      gl.uniform1i(u.u_source, texture(gl, 1, dye.read));
      gl.uniform1f(u.u_dt, dt);
      gl.uniform1f(u.u_dissipation, FLUID_TUNING.dyeDissipation);
    });
    dye.swap();
  }

  function stepFluid(entry, now) {
    const gl = entry.gl;
    if (entry.grid !== entry.canvas.width + "x" + entry.canvas.height && !allocate(entry)) {
      return;
    }

    // Capped, so a tab that was in the background for a minute does not take one enormous step and blow up.
    const dt = entry.lastStep ? Math.min((now - entry.lastStep) / 1000, 1 / 30) : 1 / 60;
    entry.lastStep = now;
    pour(entry, now, dt);
    simulate(entry, dt);

    run(entry, entry.display, null, function (u) {
      gl.uniform2f(u.u_texel, 1 / entry.dye.w, 1 / entry.dye.h);
      gl.uniform1i(u.u_dye, texture(gl, 0, entry.dye.read));
      gl.uniform2f(u.u_res, entry.canvas.width, entry.canvas.height);
      gl.uniform1f(u.u_heat, entry.heat);
      gl.uniform3f(u.u_c3, entry.c3[0], entry.c3[1], entry.c3[2]);
    });
  }

  // ── Swarm plumbing ──

  /** A program with the three particle attributes pinned, so one vertex array layout serves both passes. */
  function swarmProgram(gl, vertex, fragment, outputs) {
    const vs = compile(gl, gl.VERTEX_SHADER, vertex);
    const fs = compile(gl, gl.FRAGMENT_SHADER, fragment);
    if (!vs || !fs) {
      return null;
    }

    const prog = gl.createProgram();
    gl.attachShader(prog, vs);
    gl.attachShader(prog, fs);
    gl.bindAttribLocation(prog, 0, "a_pos");
    gl.bindAttribLocation(prog, 1, "a_vel");
    gl.bindAttribLocation(prog, 2, "a_flock");
    if (outputs) {
      // Declared before linking: these are what transform feedback writes, one particle's worth at a time.
      gl.transformFeedbackVaryings(prog, outputs, gl.INTERLEAVED_ATTRIBS);
    }

    gl.linkProgram(prog);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) {
      gl.deleteProgram(prog);
      return null;
    }

    const u = {};
    const count = gl.getProgramParameter(prog, gl.ACTIVE_UNIFORMS);
    for (let i = 0; i < count; i++) {
      const name = gl.getActiveUniform(prog, i).name;
      u[name.replace(/\[0\]$/, "")] = gl.getUniformLocation(prog, name);
    }

    return { prog: prog, u: u };
  }

  /**
   * Where the two homes are at a moment: each wandering its own side — lower down more than up, where a page usually
   * has the room — and both drifting to the middle now and then.
   */
  function homes(t, aspect) {
    const clash = Math.pow(Math.max(0, Math.sin(t * 0.11)), 6);
    const reach = aspect * (0.6 - clash * 0.55);
    return [
      -reach + Math.sin(t * 0.31) * 0.2 * aspect, -0.2 + Math.sin(t * 0.47 - 1.4) * 0.55,
      reach + Math.sin(t * 0.29 + 2.1) * 0.2 * aspect, -0.2 + Math.sin(t * 0.43 - 1.1) * 0.55,
    ];
  }

  function buildSwarm(entry) {
    const gl = entry.gl;
    const step = swarmProgram(gl, SWARM_STEP, SWARM_NOTHING, ["v_pos", "v_vel", "v_flock"]);
    const points = swarmProgram(gl, SWARM_POINTS, entry.source, null);
    if (!step || !points) {
      return false;
    }

    // Five floats a particle — position, velocity, which flock — each flock starting in a cloud round its home.
    const count = SWARM_EACH * 2;
    const seed = new Float32Array(count * 5);
    const aspect = Math.max(entry.canvas.width / Math.max(entry.canvas.height, 1), 0.5);
    const start = homes(0, aspect);
    for (let i = 0; i < count; i++) {
      const flock = i < SWARM_EACH ? 0 : 1;
      const angle = Math.random() * Math.PI * 2;
      const radius = Math.sqrt(Math.random()) * 0.2;
      seed[i * 5] = start[flock * 2] + Math.cos(angle) * radius;
      seed[i * 5 + 1] = start[flock * 2 + 1] + Math.sin(angle) * radius;
      seed[i * 5 + 2] = 0;
      seed[i * 5 + 3] = 0;
      seed[i * 5 + 4] = flock;
    }

    function pair() {
      const buffer = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      gl.bufferData(gl.ARRAY_BUFFER, seed, gl.DYNAMIC_COPY);
      const vao = gl.createVertexArray();
      gl.bindVertexArray(vao);
      gl.enableVertexAttribArray(0);
      gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 20, 0);
      gl.enableVertexAttribArray(1);
      gl.vertexAttribPointer(1, 2, gl.FLOAT, false, 20, 8);
      gl.enableVertexAttribArray(2);
      gl.vertexAttribPointer(2, 1, gl.FLOAT, false, 20, 16);
      gl.bindVertexArray(null);
      return { buffer: buffer, vao: vao };
    }

    entry.swarmStep = step;
    entry.swarmPoints = points;
    entry.particles = [pair(), pair()];

    // Let go of the generic binding: WebGL refuses a draw that writes by transform feedback into a buffer still
    // bound anywhere else, and says so only as an error nobody reads — the swarm would sit frozen where it started.
    gl.bindBuffer(gl.ARRAY_BUFFER, null);
    entry.reading = 0;
    entry.count = count;
    entry.feedback = gl.createTransformFeedback();
    entry.lastStep = 0;
    return true;
  }

  function stepSwarm(entry, now) {
    const gl = entry.gl;
    const dt = entry.lastStep ? Math.min((now - entry.lastStep) / 1000, 1 / 30) : 1 / 60;
    entry.lastStep = now;
    const t = (now - entry.start) / 1000;
    const aspect = entry.canvas.width / Math.max(entry.canvas.height, 1);
    const from = entry.particles[entry.reading];
    const to = entry.particles[1 - entry.reading];

    // The step: every particle read from one buffer and written to the other, and not a pixel drawn.
    gl.useProgram(entry.swarmStep.prog);
    gl.uniform1f(entry.swarmStep.u.u_time, t);
    gl.uniform1f(entry.swarmStep.u.u_dt, dt);
    gl.uniform1f(entry.swarmStep.u.u_aspect, aspect);
    gl.uniform2fv(entry.swarmStep.u.u_home, homes(t, aspect));
    gl.bindVertexArray(from.vao);
    gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, entry.feedback);
    gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, to.buffer);
    gl.enable(gl.RASTERIZER_DISCARD);
    gl.beginTransformFeedback(gl.POINTS);
    gl.drawArrays(gl.POINTS, 0, entry.count);
    gl.endTransformFeedback();
    gl.disable(gl.RASTERIZER_DISCARD);
    gl.bindBufferBase(gl.TRANSFORM_FEEDBACK_BUFFER, 0, null);
    gl.bindTransformFeedback(gl.TRANSFORM_FEEDBACK, null);

    // The picture: the stepped particles as soft points, added on top of one another so a crowd glows.
    gl.viewport(0, 0, entry.canvas.width, entry.canvas.height);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.ONE, gl.ONE);
    gl.useProgram(entry.swarmPoints.prog);
    gl.uniform1f(entry.swarmPoints.u.u_aspect, aspect);
    gl.uniform1f(entry.swarmPoints.u.u_size, Math.max(2, entry.canvas.height / 300));
    gl.uniform3f(entry.swarmPoints.u.u_c1, entry.c1[0], entry.c1[1], entry.c1[2]);
    gl.uniform3f(entry.swarmPoints.u.u_c2, entry.c2[0], entry.c2[1], entry.c2[2]);
    gl.bindVertexArray(to.vao);
    gl.drawArrays(gl.POINTS, 0, entry.count);
    gl.bindVertexArray(null);
    entry.reading = 1 - entry.reading;
  }

  function readColours(mount) {
    mount.c1 = tokenColour(mount.tokens[0], [0.25, 0.66, 0.96]);
    mount.c2 = tokenColour(mount.tokens[1], [1.0, 0.31, 0.55]);
    mount.c3 = tokenColour(mount.tokens[2], [1.0, 0.35, 0.35]);
  }

  // The theme toggle stamps data-theme on <html>; every mounted canvas re-reads its colours when it does.
  function watchTheme() {
    if (watchingTheme || typeof MutationObserver !== "function") {
      return;
    }

    watchingTheme = true;
    new MutationObserver(function () {
      mounts.forEach(readColours);
    }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
  }

  function resize(mount) {
    const rect = mount.host.getBoundingClientRect();
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(1, Math.round(rect.width * dpr));
    const height = Math.max(1, Math.round(rect.height * dpr));
    if (mount.canvas.width !== width || mount.canvas.height !== height) {
      mount.canvas.width = width;
      mount.canvas.height = height;
    }
  }

  function levelOf(mount) {
    try {
      if (mount.level === "watch" && window.PoAudio && typeof window.PoAudio.level === "function") {
        return window.PoAudio.level();
      }

      // The fluid pours for whoever has the floor, so while a fighter has it, it listens to the room; everything else
      // breathes with the host, the voice that belongs to the screen.
      if (mount.level === "live" && mount.fluid && Math.abs(mount.mix - 0.5) > 0.1
          && window.PoLive && typeof window.PoLive.micLevel === "function") {
        return window.PoLive.micLevel();
      }

      if (mount.level === "live" && window.PoLive && typeof window.PoLive.hostLevel === "function") {
        return window.PoLive.hostLevel();
      }
    } catch (e) {
      // A bus that will not answer reads as silence, which is what it sounds like.
    }

    return 0;
  }

  function draw(now) {
    frame = 0;
    if (mounts.size === 0) {
      return;
    }

    if (!document.hidden) {
      mounts.forEach(function (mount) {
        const gl = mount.gl;
        if (gl.isContextLost()) {
          return;
        }

        // Eased rather than taken raw: the level is sampled at whatever rate the bus updates it, and a glow that
        // steps between two values reads as a fault.
        mount.smoothed += (levelOf(mount) - mount.smoothed) * 0.18;
        if (mount.fluid) {
          stepFluid(mount, now);
          return;
        }

        if (mount.swarm) {
          stepSwarm(mount, now);
          return;
        }

        gl.viewport(0, 0, mount.canvas.width, mount.canvas.height);
        gl.useProgram(mount.program);
        gl.uniform2f(mount.u.res, mount.canvas.width, mount.canvas.height);
        gl.uniform1f(mount.u.time, (now - mount.start) / 1000);
        gl.uniform1f(mount.u.level, mount.smoothed);
        gl.uniform1f(mount.u.side, mount.side);
        gl.uniform2f(mount.u.focus, mount.focus[0], mount.focus[1]);
        gl.uniform1f(mount.u.shock, mount.shockAt > 0 ? (now - mount.shockAt) / 1000 : -1);
        gl.uniform3f(mount.u.c1, mount.c1[0], mount.c1[1], mount.c1[2]);
        gl.uniform3f(mount.u.c2, mount.c2[0], mount.c2[1], mount.c2[2]);
        gl.uniform3f(mount.u.c3, mount.c3[0], mount.c3[1], mount.c3[2]);
        gl.uniform1f(mount.u.mix, mount.mix);
        gl.uniform1f(mount.u.heat, mount.heat);
        gl.clearColor(0, 0, 0, 0);
        gl.clear(gl.COLOR_BUFFER_BIT);
        gl.drawArrays(gl.TRIANGLES, 0, 6);

        if (mount.shockAt > 0 && now - mount.shockAt > 900) {
          mount.shockAt = 0;
        }
      });
    }

    frame = requestAnimationFrame(draw);
  }

  function pump() {
    if (!frame && mounts.size > 0) {
      frame = requestAnimationFrame(draw);
    }
  }

  // Compiles the shader into the mount's context and looks up what it takes. Run at mount, and again whenever a
  // lost context is restored. False when the program will not build.
  function build(entry) {
    if (entry.fluid) {
      return buildFluid(entry);
    }

    if (entry.swarm) {
      return buildSwarm(entry);
    }

    const gl = entry.gl;
    const prog = program(gl, entry.source);
    if (!prog) {
      return false;
    }

    const buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, QUAD, gl.STATIC_DRAW);
    const position = gl.getAttribLocation(prog, "a_position");
    gl.enableVertexAttribArray(position);
    gl.vertexAttribPointer(position, 2, gl.FLOAT, false, 0, 0);
    gl.enable(gl.BLEND);
    gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);

    entry.program = prog;
    entry.u = {
      res: gl.getUniformLocation(prog, "u_res"),
      time: gl.getUniformLocation(prog, "u_time"),
      level: gl.getUniformLocation(prog, "u_level"),
      side: gl.getUniformLocation(prog, "u_side"),
      focus: gl.getUniformLocation(prog, "u_focus"),
      shock: gl.getUniformLocation(prog, "u_shock"),
      c1: gl.getUniformLocation(prog, "u_c1"),
      c2: gl.getUniformLocation(prog, "u_c2"),
      c3: gl.getUniformLocation(prog, "u_c3"),
      mix: gl.getUniformLocation(prog, "u_mix"),
      heat: gl.getUniformLocation(prog, "u_heat"),
    };
    return true;
  }

  function mount(selector, shader, options) {
    if (reducedMotion() || mounts.has(selector)) {
      return false;
    }

    const source = SHADERS[shader];
    const host = document.querySelector(selector);
    if (!source || !host) {
      return false;
    }

    const settings = options || {};
    let gl = null;
    const canvas = document.createElement("canvas");
    canvas.className = "po-gfx";
    canvas.setAttribute("aria-hidden", "true");
    // Over the page or under it. Under is the default — an aura belongs behind the faces — but grain has to be on
    // top of what it is graining, and it never takes a click either way.
    const layer = settings.over ? 3 : 0;
    canvas.style.cssText = "position:absolute;inset:0;width:100%;height:100%;display:block;pointer-events:none;z-index:" + layer + ";";
    try {
      // The swarm adds glowing points on top of one another, which is premultiplied colour by nature; everything
      // else here writes plain colour and an alpha.
      gl = canvas.getContext("webgl2", { alpha: true, antialias: false, premultipliedAlpha: shader === "swarm", powerPreference: "low-power" });
    } catch (e) {
      gl = null;
    }

    if (!gl) {
      return false;
    }

    const entry = {
      host: host,
      canvas: canvas,
      gl: gl,
      source: source,
      fluid: shader === "fluid",
      swarm: shader === "swarm",
      program: null,
      u: null,
      start: performance.now(),
      level: settings.level || null,
      // The third colour is danger for everything that runs hot, and gold for the winner's light.
      tokens: settings.tokens || ["--po-p1", "--po-p2", shader === "rays" ? "--po-accent" : "--po-danger"],
      side: 0,
      mix: 0.5,
      heat: 0,
      smoothed: 0,
      focus: [0.5, 0.5],
      shockAt: 0,
      c1: [0.25, 0.66, 0.96],
      c2: [1.0, 0.31, 0.55],
      c3: [1.0, 0.35, 0.35],
    };

    if (!build(entry)) {
      return false;
    }

    // The driver can take a context away (a laptop switching GPUs, a tab left in the background too long) and
    // give it back later, empty. Saying "wait" on the first event is what makes the second one happen; on the
    // second, everything compiled into the old context is compiled again.
    canvas.addEventListener("webglcontextlost", function (e) { e.preventDefault(); });
    canvas.addEventListener("webglcontextrestored", function () { build(entry); });

    // Sized when the host's box changes rather than measured every frame.
    if (typeof ResizeObserver === "function") {
      entry.observer = new ResizeObserver(function () { resize(entry); });
      entry.observer.observe(host);
    }

    readColours(entry);
    host.appendChild(canvas);
    resize(entry);
    mounts.set(selector, entry);
    watchTheme();
    pump();
    return true;
  }

  function unmount(selector) {
    const entry = mounts.get(selector);
    if (!entry) {
      return;
    }

    mounts.delete(selector);
    entry.host.style.removeProperty("--po-heat");
    if (entry.observer) {
      entry.observer.disconnect();
    }

    if (entry.canvas.parentNode) {
      entry.canvas.parentNode.removeChild(entry.canvas);
    }

    try {
      const lose = entry.gl.getExtension("WEBGL_lose_context");
      if (lose) {
        lose.loseContext();
      }
    } catch (e) {
      // The context is going away with the page anyway.
    }

    if (mounts.size === 0 && frame) {
      cancelAnimationFrame(frame);
      frame = 0;
    }
  }

  return {
    probe: probe,

    /**
     * A design token as a CSS colour string, resolved by this theme. Exposed because PoParticles needs the same
     * answer and there is no sense in two probes: light-dark() can only be unpicked by asking the browser.
     */
    colour: function (token) {
      const rgb = tokenColour(token, null);
      return rgb ? "rgb(" + Math.round(rgb[0] * 255) + "," + Math.round(rgb[1] * 255) + "," + Math.round(rgb[2] * 255) + ")" : null;
    },

    /** Mounts a shader on the first element matching `selector`. False when it could not be done, for any reason. */
    mount: function (selector, shader, options) {
      try {
        return mount(selector, shader, options);
      } catch (e) {
        return false;
      }
    },

    unmount: function (selector) {
      try {
        unmount(selector);
      } catch (e) {
        // Nothing left to take down.
      }
    },

    /**
     * One numeric uniform a page drives: "side", "mix" or "heat". Anything else is ignored. Heat is published on the
     * host as --po-heat as well, so the page's own CSS can run hot with the shader rather than beside it.
     */
    set: function (selector, name, value) {
      const entry = mounts.get(selector);
      if (entry && typeof value === "number" && (name === "side" || name === "mix" || name === "heat")) {
        entry[name] = value;
        if (name === "heat") {
          entry.host.style.setProperty("--po-heat", value.toFixed(3));
        }
      }
    },

    /**
     * Points the aura at a child element — measured here, once, when the speaker changes, rather than every frame:
     * a layout read per frame is the one thing that would make this expensive.
     */
    focus: function (selector, childSelector) {
      const entry = mounts.get(selector);
      if (!entry) {
        return;
      }

      const child = childSelector ? entry.host.querySelector(childSelector) : null;
      if (!child) {
        entry.focus = [0.5, 0.5];
        return;
      }

      const host = entry.host.getBoundingClientRect();
      const rect = child.getBoundingClientRect();
      if (host.width <= 0 || host.height <= 0) {
        return;
      }

      // The canvas has y running up from the bottom; the page has it running down from the top.
      entry.focus = [
        (rect.left + rect.width / 2 - host.left) / host.width,
        1 - (rect.top + rect.height / 2 - host.top) / host.height,
      ];
    },

    /** Sends a ring out from the middle. One at a time: a second slap restarts the first. */
    shock: function (selector) {
      const entry = mounts.get(selector);
      if (entry) {
        entry.shockAt = performance.now();
        pump();
      }
    },
  };
})();
