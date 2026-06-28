# Operational hardening patterns

These are notes from running MCP servers and the supervision around them as real
infrastructure rather than toys. A server that compiles and answers one request
is easy. A fleet of them, behind a multiplexing gateway, that has to stay
responsive while one backend wedges, runs out of memory, or gets a bad
configuration — that is where the interesting failures live.

Each pattern below is something I got wrong at least once and then generalised.
They are deliberately vendor-neutral: the technologies named (systemd, D-Bus,
agentgateway, OpenFGA, ext-authz, Podman/Quadlet) are public, but the patterns
matter more than any particular deployment. Read them as reusable shapes, not
as a runbook for a specific machine.

The through-line is the same in every one: **make failures local, make recovery
a property of the thing that failed, and give the least authority that still
works.**

---

## 1. Resource throttle ceilings are not reservations

**Principle.** A memory *throttle* ceiling and a memory *limit* are different
mechanisms, and confusing them produces a failure mode that looks nothing like
the one you expected. Under systemd, `MemoryHigh` is a throttle: when a process's
working set crosses it, the process is pushed into reclaim — and if its working
set genuinely needs to stay above that line, it enters *permanent* reclaim
throttle. It does not get killed. It goes unresponsive, wedged in uninterruptible
sleep (D-state), holding its sockets open while doing no useful work.

**Why it matters.** The instinct when a process is "stuck on memory" is to give
it more swap headroom. But a `swap=infinity`-style setting does nothing on a host
with no swap configured — there is nowhere to reclaim *to*. The only thing that
clears a permanent-reclaim wedge is real **headroom**: a ceiling set above the
process's actual steady-state working set. A throttle ceiling that sits *below*
what the process needs is a self-inflicted hang, not a safety net.

**How to apply.**

- Size the throttle ceiling (`MemoryHigh`) above the measured working set, not
  at an aspirational target. The ceiling is where reclaim *pressure* begins, so
  it should leave the process room to do its job.
- Pair the soft throttle with a **hard cap** (`MemoryMax`). The hard cap is the
  reservation-like backstop: cross it and the process is OOM-killed cleanly
  rather than wedged.
- Pair the hard cap with `Restart=on-failure` so a true OOM kill becomes an
  immediate, clean self-heal instead of a dead unit.
- On a swapless host, treat any "swap" tuning as a no-op and reason only about
  headroom between the working set, the throttle, and the hard cap.

The shape: a throttle for backpressure, a hard cap for safety, and an automatic
restart so the safety event repairs itself.

---

## 2. Per-unit self-heal, scoped to one unit

**Principle.** A server can fail in a way that leaves its process alive and its
listen socket open while it answers nothing — the wedge from pattern 1 is exactly
this. Recovery for that state should be a small, host-side health timer attached
to each unit, and it should probe *liveness the way callers experience it*, not
the way the kernel reports it.

**Why it matters.** A TCP connect succeeds against a frozen process, because the
kernel keeps accepting on the listen socket even when the application behind it
is doing nothing. So a connect-based health check reports "healthy" for a server
that is wedged solid. The probe has to mirror the liveness your gateway actually
uses: send a real request and treat **any** HTTP response as alive; treat **no
response within a timeout** as wedged.

**How to apply.**

- Give each server its own health timer that issues a real request on an
  interval. Any HTTP response — even an error code — means the application loop
  is running. A timeout means it is not.
- On a no-response result, **force-kill then restart**. Do not ask for a graceful
  stop: a wedged process's graceful-stop timeout can be long (on the order of 90
  seconds) before the supervisor escalates to a kill, which is 90 seconds of
  continued outage for nothing. Kill it immediately and let it come back clean.
- Decompose recovery into **per-unit actors**, each scoped to exactly one unit.
  No single component should hold "restart anything" authority — the timer for
  server A can only force-restart server A. This keeps the blast radius of a
  buggy or compromised watchdog to one unit (and sets up pattern 4).

The shape: liveness measured as a caller sees it, recovery that does not wait
politely for a process that will never answer, and authority partitioned one
unit at a time.

---

## 3. A multiplexing gateway must fail open per backend

**Principle.** A gateway that fans an MCP `initialize` handshake (or any
aggregate operation) out to all of its backends must isolate each backend's
outcome. One slow, hung, or down server must degrade only *its own* slice of the
aggregate — never stall the response for every other backend behind the same
gateway.

**Why it matters.** The default, easy-to-write behaviour is fail-*closed*: wait
for all backends, and if one never answers, the whole aggregate hangs or errors.
That turns a single bad backend into a full-gateway outage — the exact opposite
of why you put a gateway in front of a fleet. Fail-*open* per backend means a
non-responsive server is reported as unavailable while every healthy server keeps
serving.

**How to apply.**

- Make the fan-out fail-open at the per-backend boundary: a backend that errors
  or times out is dropped from the aggregate result, not propagated as a failure
  of the whole call.
- Pair fail-open with a **per-backend request timeout**. Fail-open only helps if
  a hung backend eventually *produces* something for the open path to skip; a
  connect-but-hang with no timeout still blocks forever. The timeout converts the
  hang into an error, and the error is what fail-open routes around.
- **Verify your gateway version actually implements per-backend fail-open**
  before you rely on it. This is a behaviour that varies across releases and
  configurations; assuming it and being wrong reintroduces the fail-closed
  outage you thought you had designed out. Test it by wedging one backend and
  confirming the others still answer.

The shape: per-backend isolation, a timeout to bound the worst case, and a
verified — not assumed — fail-open path.

---

## 4. Least-privilege recovery: remove the privileged actor, don't guard it

**Principle.** The component that *watches* health and the component that
*performs* recovery do not need to be the same thing, and the watcher does not
need recovery privilege at all. A monitoring or watchdog component should be an
unprivileged observe-and-alert service. Recovery belongs to the init system,
expressed per unit (patterns 1 and 2).

**Why it matters.** The tempting design gives the watchdog host-wide restart
authority — for example, by mounting the system D-Bus socket into it as root so
it can ask systemd to restart arbitrary units. That single component now holds
the keys to the whole fleet, and any bug or compromise in the most exposed,
network-facing part of the system becomes fleet-wide control. The strongest
mitigation is not to harden that actor — it is to **not have it**. A capability
that does not exist cannot be misused.

**How to apply.**

- Keep watchdogs unprivileged: they observe, they emit alerts/metrics, they do
  not hold a socket or token that lets them act on the host.
- Let recovery be a property of each unit — a per-unit health timer and restart
  policy supervised by the init system — rather than an authority handed to a
  central process.
- When you find yourself adding guards around a privileged recovery actor, ask
  first whether the actor needs to exist. Removing it beats guarding it.

The shape: separate watching from acting, push acting down into the units
themselves, and delete host-wide recovery authority rather than fencing it.

---

## 5. Unit resolution: loaded is not the same as serving

**Principle.** When you map a logical server name to the OS unit that backs it,
resolve to a unit that is *actually loaded and serving* — proven by existence
plus liveness — not to a name produced by a naming template. "The unit a guess
says should exist" and "the unit that is up and answering" are different
questions, and only the second one is useful for routing or recovery.

**Why it matters.** A disabled or dead leftover unit still reports as `loaded` to
the init system. A resolver that trusts a templated name, or that accepts the
first `loaded` match, can happily target a corpse — sending traffic or a restart
to a unit that will never serve while an actually-active unit of a slightly
different name sits ignored. Existence is necessary but not sufficient; prefer an
**active** unit over a merely `loaded` one.

**How to apply.**

- Resolve by querying for units that exist *and* pass a liveness check, and when
  more than one matches, prefer the active one over the dead/disabled one.
- **Confine resolution to the server namespace.** A resolver should only ever be
  able to name units within the set it owns — never reach into unrelated system
  units.
- Enforce a **deny-set** as a hard floor so that a resolver bug, however it
  miscomputes a name, can never target a unit outside its namespace. Defence in
  depth: the namespace confinement is the intent, the deny-set is the guarantee.

The shape: resolve by existence-plus-liveness, prefer active over loaded, and
bound the resolver so a bug cannot escape its own namespace.

---

## 6. Externalise the authorization plane, and roll it out in shadow

**Principle.** Fine-grained, per-call authorization is its own concern and
deserves its own component: a dedicated policy decision point (PDP) that the data
path consults — for example an ext-authz hook to a relationship-based access
control (ReBAC) engine — rather than authorization logic scattered inline across
every server. Externalising it makes the policy auditable, changeable, and
testable independently of the code that enforces it.

**Why it matters.** Inline-only authorization rules drift, diverge between
servers, and are impossible to reason about as a whole. A central PDP gives you
one place to express and inspect "who may do what." But a PDP is also a
single, load-bearing decision path: every request's allow/deny flows through it,
so a correctness bug — including an authorization-correctness CVE in the engine
itself — is load-bearing for *every* decision the system makes.

**How to apply.**

- Stand the PDP up in **shadow mode first**: record-only. For every call, compute
  the PDP's decision and compare it against the existing allow-list (or whatever
  authority is currently in force), but keep enforcing the old path. Let the
  comparison run until the PDP's decisions match the known-good baseline.
- Only after the shadow comparison is clean do you flip to **enforce**, where the
  PDP's decision is authoritative. Shadow-then-enforce turns "did we get the
  policy right?" into evidence instead of a leap of faith.
- **Keep the PDP patched.** Because the decision path is load-bearing for every
  authorization in the system, an authorization-correctness vulnerability in the
  engine is not a peripheral CVE — it is a direct compromise of access control.
  Treat its patch level as a first-class operational concern.

The shape: one externalised decision point, proven correct against the incumbent
in shadow before it gets to say no for real, and kept patched because everything
depends on it being right.

---

*These patterns came out of hardening real MCP infrastructure. They are offered
as reusable shapes, not as configuration to copy. The recurring lesson across all
six: localise failure, make recovery a property of the failing unit, and prefer
removing authority over guarding it.*
