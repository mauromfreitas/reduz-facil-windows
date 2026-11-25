using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ReduzFacil.Core.Model;
using ReduzFacil.Core.Queue;
using ReduzFacil.Core.Util;
using ReduzFacil.Infra;
using ReduzFacil.Views;

namespace ReduzFacil;

/// <summary>
/// Teste automático do .exe no GitHub Actions (Windows limpo). Cada verificação grava "TESTE ..." no log
/// e o script tests/smoke.ps1 confere. No fim grava "TESTE OK" (ou "TESTE FALHOU") e fecha.
/// </summary>
public sealed class SmokeRunner(App app, MainWindow window, AppServices s, SmokeOptions o)
{
    private readonly List<string> _failures = [];

    private void Check(bool ok, string what)
    {
        Log.Info($"TESTE {(ok ? "ok" : "ERRO")}: {what}");
        if (!ok) _failures.Add(what);
    }

    public async Task RunAsync()
    {
        // O padrão agora é esperar o Iniciar; o teste automático precisa que a fila ande sozinha (em todos os modos).
        s.Settings.AutoStart = true;
        s.ApplyQueueSettings();
        try
        {
            if (o.UpdateFrom is { } newExe)
            {
                // Teste da atualização: instala "newExe" como se fosse a versão nova; a nova abre e espera 3 s.
                Log.Info("TESTE atualizar: instalando " + newExe);
                AppUpdater.SwapAndRestart(newExe, ["--teste-espera", "3"]);
                Log.Info("TESTE OK");
                app.ExitApp(0);
                return;
            }
            if (o.WaitSeconds is { } wait)
            {
                await WaitModeAsync(wait);
                return;
            }
            await FullAsync();
        }
        catch (Exception ex)
        {
            Log.Error("TESTE exceção", ex);
            _failures.Add("exceção: " + ex.Message);
        }
        Log.Info(_failures.Count == 0 ? "TESTE OK" : "TESTE FALHOU: " + string.Join("; ", _failures));
        app.ExitApp(_failures.Count == 0 ? 0 : 1);
    }

    /// <summary>Fica aberto esperando arquivos de outra abertura do .exe; fecha depois de N s sem trabalho.</summary>
    private async Task WaitModeAsync(int seconds)
    {
        Log.Info("TESTE espera: pronto para receber");
        var idleSince = DateTime.UtcNow;
        var start = DateTime.UtcNow;
        while ((DateTime.UtcNow - idleSince).TotalSeconds < seconds && (DateTime.UtcNow - start).TotalMinutes < 10)
        {
            await Task.Delay(500);
            if (s.Queue.IsBusy) idleSince = DateTime.UtcNow;
        }
        foreach (var i in s.Queue.Items)
            Log.Info($"TESTE recebido: {i.Name} {i.Status} origem={i.Origin} {i.Result?.OutputPath}");
        Log.Info("TESTE OK");
        app.ExitApp();
    }

    private async Task FullAsync()
    {
        var shots = o.ShotsDir ?? Path.Combine(s.Data.Root, "capturas");
        Directory.CreateDirectory(shots);
        Check(s.Tools.Missing().Length == 0, "motores presentes: " + string.Join(",", s.Tools.Missing()));
        await s.Hardware.DetectAsync(s.Tools);
        Log.Info("TESTE placa: " + (s.Hardware.Working.Count == 0 ? "nenhuma" : string.Join(",", s.Hardware.Working.Select(w => w.Name))));
        await Shot(shots, "01-inicio");

        // 1. Reduzir todas as amostras pela tela (como se tivessem sido arrastadas)
        var samples = o.Samples!;
        window.ShowReduzir();
        window.Reduzir.AddPaths([samples]);
        await Task.Delay(800);
        await Shot(shots, "02-fila-rodando");
        var ok = await WaitIdleAsync(TimeSpan.FromMinutes(12));
        Check(ok, "fila terminou");
        foreach (var i in s.Queue.Items)
            Log.Info($"TESTE item: {i.Name} {i.Status} {i.Result?.OriginalBytes}->{i.Result?.FinalBytes} {i.Result?.EngineNote} {i.Error}");
        var items = s.Queue.Items;
        Check(items.Count >= 5, $"amostras na fila ({items.Count})");
        static bool Bad(QueueItem i) => i.Name.StartsWith("quebrado", StringComparison.OrdinalIgnoreCase) || i.Name.StartsWith("com senha", StringComparison.OrdinalIgnoreCase);
        Check(items.Where(i => !Bad(i)).All(i => i.Status is JobStatus.Done or JobStatus.KeptOriginal), "todas as amostras boas reduziram");
        Check(items.Where(Bad).All(i => i.Status == JobStatus.Failed && !string.IsNullOrEmpty(i.Error)), "quebrado e PDF com senha deram erro com mensagem");
        foreach (var i in items.Where(i => i.Status == JobStatus.Done))
            Check(File.Exists(i.Result!.OutputPath) && File.Exists(i.Path), $"saída e original existem: {i.Name}");
        await Shot(shots, "03-fila-pronta");

        // 2. Antes e depois de uma foto e de um vídeo
        foreach (var kind in new[] { FileKind.Image, FileKind.Video })
        {
            var it = items.FirstOrDefault(i => i.Kind == kind && i.Status == JobStatus.Done);
            if (it is null) continue;
            var cw = new CompareWindow(s, it.Path, it.Options, it.Result!.OutputPath) { Owner = window };
            cw.Show();
            await Task.Delay(kind == FileKind.Video ? 6000 : 3000);
            await Shot(shots, kind == FileKind.Image ? "04-comparar-foto" : "05-comparar-video", cw);
            cw.Close();
        }

        // 3. Meta (WhatsApp) num vídeo com corte
        var video = Directory.GetFiles(samples).FirstOrDefault(f => FileTypes.KindOf(f) == FileKind.Video && !Path.GetFileName(f).StartsWith("quebrado"));
        if (video is not null)
        {
            var opt = s.Settings.OptionsFor("enem");
            opt.TrimStart = TimeSpan.FromSeconds(1);
            opt.TrimEnd = TimeSpan.FromSeconds(5);
            var item = s.Queue.Add(video, opt);
            await WaitIdleAsync(TimeSpan.FromMinutes(5));
            Check(item?.Status == JobStatus.Done && item.Result!.FinalBytes <= 1024 * 1024, $"vídeo cortado cabe em 1 MB ({item?.Result?.FinalBytes})");
        }

        // 4. Onde está o peso
        window.ShowPage("peso");
        await window.Peso.ScanAsync(samples, 0.1);
        await Shot(shots, "06-onde-esta-o-peso");

        // 5. Histórico e configurações
        window.ShowPage("historico");
        await Task.Delay(500);
        Check(s.History.Totals.SavedBytes > 0, "histórico somou economia");
        await Shot(shots, "07-historico");
        window.ShowPage("config");
        await Task.Delay(500);
        await Shot(shots, "08-configuracoes");

        // 6. Menu do botão direito: instala, confere, remove
        WindowsIntegration.InstallShellMenu();
        Check(WindowsIntegration.IsShellMenuInstalled(out var mine) && mine, "menu do Explorer instalado");
        WindowsIntegration.RemoveShellMenu();
        Check(!WindowsIntegration.IsShellMenuInstalled(out _), "menu do Explorer removido");

        // 7. Pasta vigiada
        // Fora da ReduzFacil-dados: a pasta vigiada ignora de propósito o que estiver lá dentro.
        var watch = Path.Combine(s.Data.ExeDir, "teste-vigiada");
        Directory.CreateDirectory(watch);
        var w1 = new Core.Settings.WatchSettings { Enabled = true, Folder = watch, Profile = "qualidade" };
        var watch2 = Path.Combine(s.Data.ExeDir, "teste-vigiada-2");
        Directory.CreateDirectory(watch2);
        var w2 = new Core.Settings.WatchSettings { Enabled = true, Folder = watch2, Profile = "enem" };
        s.Settings.Watches.Add(w1);
        s.Settings.Watches.Add(w2);
        s.RestartWatcher();
        Check(s.WatchCount == 2, $"duas pastas vigiadas ligaram ({s.WatchCount})");
        window.ShowPage("config");
        await Task.Delay(600);
        await Shot(shots, "10-pastas-vigiadas");
        Check(s.WatchRunning, "pasta vigiada ligou");
        var photo = Directory.GetFiles(samples).FirstOrDefault(f => FileTypes.KindOf(f) == FileKind.Image && new FileInfo(f).Length > 1_000_000);
        if (photo is not null)
        {
            var before = s.Queue.Items.Count;
            File.Copy(photo, Path.Combine(watch, "nova foto.jpg"));
            var until = DateTime.UtcNow.AddSeconds(30);
            while (s.Queue.Items.Count == before && DateTime.UtcNow < until) await Task.Delay(300);
            await WaitIdleAsync(TimeSpan.FromMinutes(2));
            var got = s.Queue.Items.LastOrDefault(i => i.Origin == JobOrigin.Watch);
            Check(got?.Status == JobStatus.Done, $"pasta vigiada reduziu a foto nova ({got?.Status})");
            await Task.Delay(4000); // o arquivo gerado não pode entrar de novo
            Check(s.Queue.Items.Count(i => i.Origin == JobOrigin.Watch) == 1, "saída da pasta vigiada não entrou de novo");
            // Segunda pasta, outro perfil (Enem = 1 MB)
            File.Copy(photo, Path.Combine(watch2, "inscricao.jpg"));
            var until2 = DateTime.UtcNow.AddSeconds(30);
            while (s.Queue.Items.Count(i => i.Origin == JobOrigin.Watch) < 2 && DateTime.UtcNow < until2) await Task.Delay(300);
            await WaitIdleAsync(TimeSpan.FromMinutes(2));
            var got2 = s.Queue.Items.LastOrDefault(i => i.Origin == JobOrigin.Watch);
            Check(got2?.Status == JobStatus.Done && got2.Result!.FinalBytes <= 1024 * 1024 && got2.Name == "inscricao.jpg",
                $"segunda pasta usou o perfil dela ({got2?.Name} {got2?.Result?.FinalBytes})");
        }
        s.Settings.Watches.Clear();
        s.RestartWatcher();

        // Ferramentas de PDF: juntar fotos e separar páginas
        window.ShowPage("ferramentas");
        var imgs = Directory.GetFiles(samples).Where(f => FileTypes.KindOf(f) == FileKind.Image && !Path.GetFileName(f).StartsWith("icone")).Take(3).ToList();
        window.Ferramentas.AddFiles(imgs);
        await Shot(shots, "11-ferramentas");
        var joined = await window.Ferramentas.JoinToAsync(Path.Combine(s.Data.ExeDir, "fotos juntas.pdf"));
        Check(joined is not null && File.Exists(joined), "juntou fotos num PDF");
        if (joined is not null)
        {
            var parts = await window.Ferramentas.SplitAsync(null, joined);
            Check(parts.Count == imgs.Count, $"separou uma página por arquivo ({parts.Count})");
        }

        // Repetidos
        var dupDir = Path.Combine(s.Data.ExeDir, "teste-repetidos");
        Directory.CreateDirectory(dupDir);
        if (photo is not null)
        {
            File.Copy(photo, Path.Combine(dupDir, "a.jpg"), true);
            File.Copy(photo, Path.Combine(dupDir, "copia de a.jpg"), true);
        }
        window.ShowPage("peso");
        window.Peso.ShowDuplicatesMode();
        await window.Peso.FindDuplicatesAsync(dupDir, true);
        await Shot(shots, "12-repetidos");

        // Ctrl+V de uma imagem (a área de transferência pode não existir no servidor de teste: só registra)
        try
        {
            window.ShowReduzir();
            var bmp = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 800, 600));
            bmp.Render(dv);
            Clipboard.SetImage(bmp);
            var before = s.Queue.Items.Count;
            ApplicationCommands.Paste.Execute(null, window);
            await WaitIdleAsync(TimeSpan.FromMinutes(1));
            var pasted = s.Queue.Items.Skip(before).FirstOrDefault(i => i.Origin == JobOrigin.Paste);
            Log.Info($"TESTE colar: {pasted?.Name} {pasted?.Status} copiado={Clipboard.ContainsFileDropList()}");
        }
        catch (Exception ex)
        {
            Log.Info("TESTE colar: área de transferência indisponível aqui (" + ex.Message + ")");
        }

        // Tema escuro
        ThemeManager.Apply("escuro");
        window.ShowReduzir();
        await Task.Delay(600);
        await Shot(shots, "13-escuro-reduzir");
        window.ShowPage("config");
        await Task.Delay(600);
        await Shot(shots, "14-escuro-configuracoes");
        ThemeManager.Apply("claro");

        // 8. Pausa
        if (video is not null)
        {
            var it = s.Queue.Add(video, s.Settings.OptionsFor("qualidade"))!;
            var until = DateTime.UtcNow.AddSeconds(30);
            while (it.Progress < 0.05 && DateTime.UtcNow < until) await Task.Delay(100);
            s.Queue.PauseAll();
            await Task.Delay(500);
            var p1 = it.Progress;
            await Task.Delay(2500);
            Check(it.Progress - p1 < 0.02, $"pausa segurou o vídeo ({p1:0.00}→{it.Progress:0.00})");
            await Shot(shots, "09-pausado");
            s.Queue.ResumeAll();
            await WaitIdleAsync(TimeSpan.FromMinutes(5));
            Check(it.Status == JobStatus.Done, "continuou depois da pausa");
        }
    }

    private async Task<bool> WaitIdleAsync(TimeSpan max)
    {
        var until = DateTime.UtcNow + max;
        while (s.Queue.IsBusy)
        {
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(300);
        }
        return true;
    }

    private static async Task Shot(string dir, string name, Window? w = null)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        var win = w ?? Application.Current.MainWindow;
        if (win?.Content is not FrameworkElement root || root.ActualWidth < 1) return;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(win);
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle((Brush)Application.Current.Resources["Bg"], null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(bg);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(dir, name + ".png"));
            enc.Save(fs);
            Log.Info("TESTE captura: " + name);
        }
        catch (Exception ex)
        {
            Log.Warn("Captura falhou: " + ex.Message);
        }
    }
}
