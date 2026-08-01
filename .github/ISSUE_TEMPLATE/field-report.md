---
name: Field report from an Eclipse installation
about: A metric read N/A, a number looks wrong, or the plugin did not run
title: ''
labels: field-report
---

<!--
Everything this plugin does goes through the Varian API, and that API differs between
Eclipse versions. Most reports resolve to one property having a different name on your
installation, which is a one-line fix once the name is known. The Diagnostics tab is where
that name is.
-->

## Environment

- **Eclipse version:**
- **Plugin version:** <!-- title bar of the window -->
- **Modality pair:** <!-- CT-CT, CT-MR, CT-CBCT ... -->
- **Registration type:** <!-- rigid / deformable -->

## What happened

<!-- What you expected, and what the plugin showed instead. -->

## The Diagnostics tab

<!--
Paste it, or the relevant entries. This is the single most useful thing you can attach.

If a metric read N/A, the entry naming the failed operation is the one that matters.
If the registration matrix could not be read at all, the diagnostics contain the
registration object's type and full member list -- that is exactly what is needed to
probe the right property by name.
-->

```
```

## If a number looks wrong rather than missing

Eclipse can answer some of these itself, which turns a suspicion into a comparison:

- [ ] **Rigid translation and rotation** — `Properties → Tech (Reg) → Rigid Registration`
- [ ] **Jacobian, divergence, distance, curl, per-component displacement** — Eclipse's own
      deformation field views, each with its colour-bar range
- [ ] **DSC and structure volumes** — Eclipse's DICOM statistics table
- [ ] **Effective sampling and overlap** — stated in the plugin's own criterion column

Attaching Eclipse's number beside the plugin's is worth more than any description of the
discrepancy.

## Anything else

<!-- Anonymised exported report or CSV row, if you can share one. No patient identifier is
written to either, but check against your institution's policy before attaching. -->
