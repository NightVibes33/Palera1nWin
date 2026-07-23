# Dark UI contrast regression

Code-built cards must use the shared `Brush.Card`, `Brush.SurfaceSecondary`, `Brush.SurfaceTertiary`, `Brush.Border`, and text brushes. `ProgrammaticTheme.ApplyCard` applies the correct dark surface and fills any text that would otherwise inherit the Windows default black foreground.

Onboarding content version 2 matches the four-action downgrade interface and resets prior guide completion once so existing users see the corrected instructions.
