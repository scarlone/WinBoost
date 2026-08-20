using WinBoost.Core;
using Xunit;

namespace WinBoost.Tests;

public class WindowPlacementTests
{
    /// <summary>1920x1080 al 150%: l'area di lavoro reale della macchina di sviluppo.</summary>
    private static readonly ScreenRect FullHd150 = new(0, 0, 1280, 672);

    [Fact]
    public void LaFinestraNonEsceMaiDallAreaDiLavoro()
    {
        // Il caso che ha prodotto il difetto: 820 DIP di altezza su 672 disponibili.
        var placed = WindowPlacement.FitToWorkArea(1240, 820, FullHd150);

        Assert.True(placed.Top >= FullHd150.Top, $"Top {placed.Top} sopra il bordo superiore");
        Assert.True(placed.Left >= FullHd150.Left);
        Assert.True(placed.Bottom <= FullHd150.Bottom);
        Assert.True(placed.Right <= FullHd150.Right);
    }

    [Fact]
    public void IlBordoSuperioreNonEMaiNegativo()
    {
        // Prima della correzione questo valeva -74: la barra del titolo era irraggiungibile.
        Assert.True(WindowPlacement.FitToWorkArea(1240, 820, FullHd150).Top >= 0);
    }

    [Theory]
    [InlineData(1280, 672)]     // 1920x1080 @ 150%
    [InlineData(1097, 576)]     // 1920x1080 @ 175%
    [InlineData(1920, 1032)]    // 1920x1080 @ 100%
    [InlineData(1366, 728)]     // portatile 1366x768 @ 100%
    [InlineData(800, 450)]      // schermo molto piccolo
    public void SuOgniRisoluzionePlausibileLaFinestraEInteramenteVisibile(double w, double h)
    {
        var area = new ScreenRect(0, 0, w, h);
        var placed = WindowPlacement.FitToWorkArea(1240, 760, area, minWidth: 860, minHeight: 480);

        Assert.InRange(placed.Left, area.Left, area.Right);
        Assert.InRange(placed.Top, area.Top, area.Bottom);
        Assert.True(placed.Right <= area.Right + 0.001);
        Assert.True(placed.Bottom <= area.Bottom + 0.001);
    }

    [Fact]
    public void IMinimiNonPossonoRendereLaFinestraPiuGrandeDelloSchermo()
    {
        // Un minimo piu' alto dello schermo deve cedere: e' il minimo a essere sbagliato,
        // non lo schermo.
        var area = new ScreenRect(0, 0, 700, 400);
        var placed = WindowPlacement.FitToWorkArea(1240, 760, area, minWidth: 860, minHeight: 480);

        Assert.True(placed.Width <= area.Width);
        Assert.True(placed.Height <= area.Height);
        Assert.True(placed.Top >= 0);
    }

    [Fact]
    public void UnaFinestraCheCiStaVieneCentrataSenzaRimpicciolirsi()
    {
        var area = new ScreenRect(0, 0, 1920, 1032);
        var placed = WindowPlacement.FitToWorkArea(1240, 760, area);

        Assert.Equal(1240, placed.Width);
        Assert.Equal(760, placed.Height);
        Assert.Equal((1920 - 1240) / 2, placed.Left);
        Assert.Equal((1032 - 760) / 2, placed.Top);
    }

    [Fact]
    public void UnAreaDiLavoroSpostataViaRispettata()
    {
        // Schermo secondario a destra, o taskbar in alto: l'origine non e' (0,0).
        var area = new ScreenRect(1920, 40, 1280, 672);
        var placed = WindowPlacement.FitToWorkArea(1240, 820, area);

        Assert.True(placed.Left >= area.Left);
        Assert.True(placed.Top >= area.Top);
        Assert.True(placed.Right <= area.Right);
        Assert.True(placed.Bottom <= area.Bottom);
    }

    [Fact]
    public void ResteUnMargineDaiBordi()
    {
        var placed = WindowPlacement.FitToWorkArea(1240, 820, FullHd150);

        // L'altezza e' stata ridotta: deve restare lo spazio del margine.
        Assert.Equal(FullHd150.Height - WindowPlacement.DefaultMargin, placed.Height);
    }

    [Fact]
    public void UnAreaDegenereNonProduceDimensioniNegative()
    {
        var placed = WindowPlacement.FitToWorkArea(1240, 760, new ScreenRect(0, 0, 10, 10));

        Assert.True(placed.Width >= 0);
        Assert.True(placed.Height >= 0);
    }
}
