# Verification readings behind the patch

Machine: Windows 11 (10.0.26300), Intel iGPU driving a 3840x2160 **144 Hz** panel
(declared 144 Hz via `EnumDisplaySettings`, and the patch's own calibration reads
144.02 Hz on the quiet runs). MSVC 14.51, FFmpeg 7.x import libraries from the vendored
tree in the kernel repo.

Everything below was produced by the tools in the harness repository
(`content_period_probe`, `present_pacing_probe`, `generate_samples.sh --verify`), which are
not part of this patch — the patch only carries kernel and host-interop changes. The probe
sources are the same ones that produced these logs; they are included in `verify/` output
only, so a reader can check the numbers against the described operations.

## What passes

| # | Reading | Value |
|---|---|---|
| 1 | Fixture self-checks (rate-band per segment, zero remainder, bare-stream shape) | 9 of 9 pass |
| 2 | Windowless content-period probe, 5 arms | 0 failing arms |
| 3 | 25+40 fps `.ts` / `.mp4` — plateau series | two plateaus each: `[0.00s 2.06s @20.0Hz][2.68s 1.87s @40.0Hz]`, drops 0 |
| 4 | 20 fps `.ts` / `.mp4` (constant-rate control) | one plateau each, `[0.51s 3.90s @20.0Hz]` |
| 5 | bare Annex-B 20 fps (no PTS anywhere) | plays; one plateau at its synthesized rate; `frameRateMode` reads `variable` at open and `constant` after playback |
| 6 | 60 fps ceiling / realised rate | ceiling 60.00 Hz, flat over the window (0 rise steps, 0 fall steps), one rate plateau `[7.41s @60.0Hz]`, flips per presented frame 1.000 |
| 7 | 240 fps ceiling | ceiling 144.00 Hz (panel wins), 528 surplus frames all in the display-rate bucket, pipeline bucket 0 |
| 8 | 120 fps ceiling and hold band | ceiling 120.00 Hz, plateau `[4.93s @119.9Hz]`, **hold band 99.9% in band, worst deviation 1 refresh** |
| 9 | 25+60 fps `.ts` with the windowed probe | ceiling rises once (25 -> 60) and **never falls**; `PASS 判13/红0/未判6` |
| 10 | Variable-refresh (unsynchronised) arm | `gridLocked` 0 with `bypass` 1 in every poll, ceiling still reported 60.00 Hz, holds 97.7-99.8% in band |

`verify/pr_battery.log`, `verify/pr_selfcheck.log`, `verify/pr_sweep.log` (20:41 batch) and
`verify/pr_sweep_postfix.log` (20:53 re-run of the 24 / vrr / 120 arms) hold the raw output.

## The paired A/B for the removed slot aiming

`12e_cfr_120.mp4` (120 fps on a 144 Hz panel = 1.200 refreshes per frame, so the accepted
band is {1,2}) is the case where choosing which vblank a flip lands on could plausibly
matter. Because a desktop application was holding 300%+ CPU all evening, the comparison was
run **paired inside one load window**, alternating new -> old -> new -> old kernel, using a
single probe executable for both arms and verifying the swap-chain DLL's SHA-256 after every
swap (`new 091688cd10eef54e`, `old 1be4eff38d97d65c`) so the two arms cannot silently be
the same binary. Logs: `verify/ab120_*.out`.

| arm | kernel | presented | reached display | never composited | hold band | worst |
|---|---|---|---|---|---|---|
| new1 | ceiling only | 81.9 fps | 76.5 fps | **6.7%** | 99.7% | 1 |
| old1 | with aiming | 81.4 fps | 62.5 fps | **23.3%** | 99.8% | 1 |
| new2 | ceiling only | 82.5 fps | 78.9 fps | **4.5%** | 99.9% | 1 |
| old2 | with aiming | 81.5 fps | 57.5 fps | **29.6%** | 100.0% | 0 |

Read as a relative difference only. Absolute throughput in these runs is not a kernel
verdict: 82 fps presented against a 120 Hz ceiling is the machine, not the patch.

## Ablation, which is what gives the judges teeth

* Restoring the old two-tier content-period source collapses the `.ts` arm to a single
  20 Hz plateau (back-half judges red) while the `.mp4` arm stays green — the red comes
  from what the container does not carry, not from a broken probe.
* Disabling the ceiling's upward revision leaves the reported ceiling at 20 Hz for the
  20+40 `.ts`, and the "ceiling is not below the fastest segment" judge goes red on
  exactly that. The same run still presents 40.1 fps with zero drops, which is why the
  commit message limits the harm to the *reported* value rather than claiming frames were
  throttled.
* Disabling the byte-seek fallback for position 0 returns the bare stream to 0 decoded
  frames with the session marked ended.

## Known-open items

* A 24 fps run taken at 20:53 under the same desktop load read 76.6% in band with a worst
  deviation of 10 refreshes. Two causes are mixed there and neither is settled: the load,
  and the fact that 24 fps on 144.02 Hz is an **integral** ratio (6.000), so the
  floor/ceiling band collapses to a single accepted value and every one-refresh phase slip
  counts as out of band. The band's shape is not changed in this patch.
* With the panel limiting the rate (240 fps content), the "production kept up" precondition
  can never be met — discarding is exactly what keeps the realized rate below the ceiling —
  so the three timing judges read undecided on that arm and its throughput is judged by the
  discard-accounting pair instead.
* Absolute timing readings taken while other processes were active are marked undecided
  throughout; a quiet re-run of the three constant-rate arms is still outstanding.
