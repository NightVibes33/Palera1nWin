# Onboarding v3 requirement

The tested Windows package must automatically open the full four-section onboarding guide on first launch after the v3 content update.

The guide covers Overview, Jailbreak, Downgrade, and Cold Boot. The Downgrade section documents the visible four-action DarkSword Quick Actions layout:

1. Start Downgrade
2. Test DFU → Pwned/Pongo
3. Boot Device
4. Import Boot Profile

The main window must invoke `OnboardingWindow.ShowFirstRun(this)` after initial navigation. Existing v2 completion state is migrated by advancing `CurrentContentVersion` to 3 so users receive the corrected guide once.
