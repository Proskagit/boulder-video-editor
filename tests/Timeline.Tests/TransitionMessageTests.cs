using AiVideoEditor.Core.Common;
using AiVideoEditor.Core.Entities;
using AiVideoEditor.Core.Interfaces;
using AiVideoEditor.Core.Localization;
using Xunit;

namespace AiVideoEditor.Timeline.Tests;

/// <summary>
/// The Phase 28 manual run (D2, D5, D6): a message about a transition names its actual type — «Переход «Сдвиг» удалён», not
/// «Наплыв удалён» — on every path that names one: the automatic removal (D025 §5), the refusal to add (the 1.0 s rule
/// among others), a cut that already has a transition, a type or duration change, a split, an insert or an overwrite
/// inside its zone, a trim or a slip it stops, a speed change undone, two clips that don't meet. A dissolve and a
/// crossfade keep their own words (D025, D032). Every text is checked in English and in Russian.
/// </summary>
public class TransitionMessageTests
{
    private static MediaTime F(TimelineFixture f, long n) => MediaTime.FromFrame(n, f.Rate);
    private static void Ok(TimelineEditResult r) => Assert.True(r.Success, r.Message);

    /// <summary>Images on V1 at 30 fps (unlimited handles), touching, of the given lengths in frames.</summary>
    private static (TimelineFixture F, List<ImageClip> Clips) Images(params long[] lengths)
    {
        var f = new TimelineFixture();
        var image = f.Image();
        var clips = new List<ImageClip>();
        long at = 0;
        foreach (var length in lengths)
        {
            clips.Add(f.PlaceImageFrames(f.V1, image, at, at + length, f.Rate));
            at += length;
        }
        return (f, clips);
    }

    /// <summary>A 20 s video at 25 fps (500 frames) on V1 split at the given frames: every cut has media on both sides.</summary>
    private static (TimelineFixture F, MediaAsset Video, List<Clip> Clips) Video(params long[] cuts)
    {
        var f = new TimelineFixture();
        var video = f.Video(20, FrameRate.Fps25);
        Ok(f.Service.AddClip(video.Id));
        foreach (var cut in cuts) Ok(f.Service.Split(MediaTime.FromFrame(cut, FrameRate.Fps25)));
        return (f, video, f.V1.Clips.OrderBy(c => c.TimelineStart).ToList());
    }

    private static Transition Add(TimelineFixture f, Clip a, Clip b, string type)
    {
        var result = f.Service.AddTransition(a.Id, b.Id, type);
        Ok(result);
        return f.V1.Transitions.Single(t => t.Id == result.TransitionId);
    }

    /// <summary>Runs <paramref name="check"/> with the interface language set to <paramref name="language"/> (D039).</summary>
    private static void In(string language, Action check)
    {
        var before = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(language);
        try { check(); }
        finally { System.Globalization.CultureInfo.CurrentUICulture = before; }
    }

    // --- the automatic removal (D2) -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", "push", "The transition \"Push\" was removed: its clips no longer meet.")]
    [InlineData("ru", "push", "Переход «Сдвиг» удалён: его клипы больше не соседние.")]
    [InlineData("ru", "flash", "Переход «Вспышка» удалён: его клипы больше не соседние.")]
    [InlineData("ru", "dipToBlack", "Переход «Затемнение» удалён: его клипы больше не соседние.")]
    [InlineData("ru", "zoom", "Переход «Наезд» удалён: его клипы больше не соседние.")]
    [InlineData("en", "crossDissolve", "A dissolve was removed: its clips no longer meet.")]
    [InlineData("ru", "crossDissolve", "Наплыв удалён: его клипы больше не соседние.")]
    public void A_transition_removed_with_its_cut_is_named_by_its_type(string language, string type, string expected) => In(language, () =>
    {
        var (f, clips) = Images(100, 100);
        Add(f, clips[0], clips[1], type);

        var result = f.Service.DeleteClips(new[] { clips[0].Id });

        Ok(result);
        Assert.Equal(expected, result.Message);
    });

    [Theory]
    [InlineData("en", "push", "2 transitions were removed: their clips no longer meet.")]
    [InlineData("ru", "push", "Удалено переходов: 2 — их клипы больше не соседние.")]
    [InlineData("en", "crossDissolve", "2 dissolves were removed: their clips no longer meet.")]
    public void Several_removed_together_are_counted_as_transitions_unless_all_are_dissolves(string language, string first, string expected) =>
        In(language, () =>
        {
            var (f, clips) = Images(100, 100, 100);
            Add(f, clips[0], clips[1], first);
            Add(f, clips[1], clips[2], TransitionRules.CrossDissolve);

            var result = f.Service.DeleteClips(new[] { clips[1].Id });

            Ok(result);
            Assert.Equal(expected, result.Message);
        });

    // --- adding (D5, D6) ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", "flash", "Can't add the transition \"Flash\": Flashes and dips to black must be at least 1 second apart.")]
    [InlineData("ru", "flash", "Нельзя добавить переход «Вспышка»: Вспышки и затемнения должны быть не ближе 1 секунды друг к другу.")]
    [InlineData("ru", "dipToBlack", "Нельзя добавить переход «Затемнение»: Вспышки и затемнения должны быть не ближе 1 секунды друг к другу.")]
    public void The_one_second_refusal_names_the_type_being_added(string language, string type, string expected) => In(language, () =>
    {
        var (f, clips) = Images(100, 20, 100);
        Add(f, clips[0], clips[1], TransitionRules.Flash);

        var result = f.Service.AddTransition(clips[1].Id, clips[2].Id, type);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
    });

    [Theory]
    [InlineData("en", "zoom", "This cut already has a transition: \"Zoom\".")]
    [InlineData("ru", "zoom", "На этой склейке уже есть переход «Наезд».")]
    [InlineData("ru", "push", "На этой склейке уже есть переход «Сдвиг».")]
    [InlineData("en", "crossDissolve", "This cut already has a dissolve.")]
    [InlineData("ru", "crossDissolve", "На этой склейке уже есть наплыв.")]
    public void A_cut_that_has_a_transition_names_the_one_it_has(string language, string existing, string expected) => In(language, () =>
    {
        var (f, clips) = Images(100, 100);
        Add(f, clips[0], clips[1], existing);

        Assert.Equal(expected, f.Service.AddTransition(clips[0].Id, clips[1].Id, TransitionRules.Flash).Message);
        Assert.Equal(expected, f.Service.AddTransition(clips[0].Id, clips[1].Id, F(f, 12)).Message);   // the frames path (D025)
    });

    [Theory]
    [InlineData("en", "flash", "The transition \"Flash\" needs two clips that meet: the first must end where the second starts.")]
    [InlineData("ru", "push", "Для перехода «Сдвиг» нужны два соседних клипа: первый должен заканчиваться там, где начинается второй.")]
    [InlineData("en", "crossDissolve", "A dissolve needs two clips that meet: the first must end where the second starts.")]
    public void Clips_that_do_not_meet_are_refused_naming_the_type(string language, string type, string expected) => In(language, () =>
    {
        var f = new TimelineFixture();
        var image = f.Image();
        var a = f.PlaceImageFrames(f.V1, image, 0, 30, f.Rate);
        var b = f.PlaceImageFrames(f.V1, image, 40, 70, f.Rate);

        Assert.Equal(expected, f.Service.AddTransition(a.Id, b.Id, type).Message);
    });

    // --- changing -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", "Can't change the transition \"Zoom\": Flashes and dips to black must be at least 1 second apart.")]
    [InlineData("ru", "Нельзя изменить переход «Наезд»: Вспышки и затемнения должны быть не ближе 1 секунды друг к другу.")]
    public void A_type_change_refused_names_the_transition_changed(string language, string expected) => In(language, () =>
    {
        var (f, clips) = Images(100, 20, 100);
        Add(f, clips[0], clips[1], TransitionRules.DipToBlack);
        var zoom = Add(f, clips[1], clips[2], TransitionRules.Zoom);

        Assert.Equal(expected, f.Service.SetTransitionType(zoom.Id, TransitionRules.Flash).Message);
    });

    // --- edits a zone refuses or stops ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("en", "push", "Can't split inside the transition \"Push\".")]
    [InlineData("ru", "push", "Нельзя разрезать внутри перехода «Сдвиг».")]
    [InlineData("en", "crossDissolve", "Can't split inside a dissolve.")]
    public void A_split_inside_a_zone_names_its_transition(string language, string type, string expected) => In(language, () =>
    {
        var (f, clips) = Images(100, 100);
        Add(f, clips[0], clips[1], type);                                                 // 9 or 30 frames around frame 100

        Assert.Equal(expected, f.Service.Split(F(f, 102)).Message);
    });

    [Theory]
    [InlineData("en", "Can't insert inside the transition \"Push\".", "Can't overwrite inside the transition \"Push\".")]
    [InlineData("ru", "Нельзя вставить внутрь перехода «Сдвиг».", "Нельзя заменить внутри перехода «Сдвиг».")]
    public void Insert_and_overwrite_inside_a_zone_name_its_transition(string language, string insert, string overwrite) => In(language, () =>
    {
        var (f, video, clips) = Video(100);
        Ok(f.Service.SetTransitionDuration(Add(f, clips[0], clips[1], TransitionRules.Push).Id, F(f, 20)));   // zone 90–110

        Assert.Equal(insert, f.Service.InsertClip(video.Id, F(f, 0), F(f, 30), F(f, 103)).Message);
        Assert.Equal(overwrite, f.Service.OverwriteClip(video.Id, F(f, 0), F(f, 45), F(f, 105)).Message);
    });

    [Theory]
    [InlineData("en", "The trim stopped where the transition \"Push\" needs the clip's frames.")]
    [InlineData("ru", "Обрезка остановлена там, где кадры клипа нужны переходу «Сдвиг».")]
    public void A_ripple_trim_stopped_by_a_transition_names_it(string language, string expected) => In(language, () =>
    {
        var (f, _, clips) = Video(100, 300);
        Add(f, clips[0], clips[1], TransitionRules.Push);                                 // A needs 5 frames after it

        var result = f.Service.RippleTrimClip(clips[0].Id, ClipEdge.End, F(f, 1000));

        Ok(result);
        Assert.Equal(expected, result.Message);
    });

    [Theory]
    [InlineData("en", "The slip stopped where the transition \"Push\" needs the clip's source.")]
    [InlineData("ru", "Сдвиг остановлен там, где материал клипа нужен переходу «Сдвиг».")]
    public void A_slip_stopped_by_a_transition_names_it(string language, string expected) => In(language, () =>
    {
        var (f, _, clips) = Video(100, 300);
        Add(f, clips[0], clips[1], TransitionRules.Push);                                 // B needs 4 frames before it

        var result = f.Service.SlipClip(clips[1].Id, -1000);

        Ok(result);
        Assert.Equal(expected, result.Message);
    });

    [Theory]
    [InlineData("en", "The transition \"Flash\" is back: its clips meet again.")]
    [InlineData("ru", "Переход «Вспышка» восстановлен: его клипы снова соседние.")]
    public void A_speed_change_typed_back_restores_the_transition_by_its_name(string language, string expected) => In(language, () =>
    {
        var (f, _, clips) = Video(100, 300);
        Add(f, clips[0], clips[1], TransitionRules.Flash);
        Ok(f.Service.SetClipSpeed(clips[0].Id, ClipSpeed.FromSteps(40)));                // A's end moves: the flash goes
        Assert.Empty(f.V1.Transitions);

        var back = f.Service.SetClipSpeed(clips[0].Id, ClipSpeed.Normal);

        Ok(back);
        Assert.Equal(expected, back.Message);
        Assert.Single(f.V1.Transitions);
    });
}
