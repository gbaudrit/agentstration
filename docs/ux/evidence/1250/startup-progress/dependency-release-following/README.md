# Following when a Bootstrap dependency becomes available

Implementation evidence for PR #1260 / #1250, captured on 2026-11-21.
This extends the earlier viewport work after the maintainer clarified that
“resume” means Bootstrap continuing when its required model becomes available.
It is not maintainer UX approval; reusable child #1251 remains gated by explicit
UX validation and merge.

## Reproduction and corrected behavior

The Release standalone executable uses isolated SQLite, the disposable local
Development administrator, the actual SharedKeyFile Ollama extension and a
controllable offline upstream. A first Instance Model Profile is already
available; a second Tenant Model Profile waits for its required model. An access
policy separates their actual resource groups. A Workspace Agent and 512 Tenant
Parameters follow them.

The browser checks wait for the interactive Console before exercising controls:

1. Scroll the preparation list; following remains enabled.
2. Select the completed first Model Profile. Its details remain visible while
   the second profile waits. This deliberately pauses selection following.
3. Make the required model available upstream, without clicking the follow
   action. The next observation of resumed execution re-enables following and
   selects the active Agent group, then the Parameter group. Navigation and
   detail panes agree on the selected group.
4. Complete Bootstrap. Results remain visible; opening the Console still
   requires an explicit action.

This sequence passed on desktop French (1660 × 900) and mobile French
(390 × 844). The mobile run checked horizontal overflow and reported no
JavaScript errors. Desktop captures show the selected next step inside the
navigation viewport. The fixture and upstream require no live model service or
model download.

## Captures

- [Inspecting a completed profile during the wait](inspecting-while-waiting-fr.png)
- [Automatically following the next group](automatically-following-next-fr.png)
- [Completed results retained](resumed-completed-fr.png)
- [Mobile inspection](inspecting-while-waiting-mobile-fr.png)
- [Mobile automatic continuation](automatically-following-next-mobile-fr.png)
- [Mobile completed results](resumed-completed-mobile-fr.png)

## Regression checks and scope

Component regressions cover inspection during a dependency wait, automatic
return to following after its release and continued selection through Agent and
Flow groups. Explicit follow also continues through subsequent active-step
changes with a bound parent. Resource search/state/page filters are cleared when
following resumes so they cannot hide the current resource.

Layout JavaScript no longer sends implicit pause callbacks on wheel, touch,
pointer or panel keyboard events. Step selection and explicit resource
inspection still own their pauses. Ordinary updates while resources are being
applied preserve inspection; a terminal failure does not trigger automatic
resumption. Safe diagnostics, startup admission, backend execution ordering and
the explicit completion action retain their existing contracts.

Only screenshots and this explanation are committed. No database, shared key,
server log or temporary fixture manifest is included. Actual validation totals
are recorded in the PR. No reusable browser journey was introduced before the
existing UX validation/merge gate.
