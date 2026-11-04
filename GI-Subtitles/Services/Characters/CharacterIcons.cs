using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GI_Subtitles.Services.Characters
{
    /// <summary>
    /// Fills the overview's icon paths from <see cref="CharacterImageCache"/>. Views render first with
    /// what is on disk, then call <see cref="FetchMissingAsync"/> and re-render when it returns true.
    /// </summary>
    public static class CharacterIcons
    {
        public static void ApplyCached(CharacterOverviewModel model, CharacterImageCache cache)
        {
            if (model == null || cache == null) return;
            model.IconPath = cache.CachedPathOrNull(model.IconUrl);
            foreach (var set in Sets(model)) set.IconPath = cache.CachedPathOrNull(set.IconUrl);
            foreach (var weapon in model.Weapons) weapon.IconPath = cache.CachedPathOrNull(weapon.IconUrl);
            foreach (var mate in model.TeammateCards) mate.IconPath = cache.CachedPathOrNull(mate.IconUrl);
            foreach (var h in model.Highlights) h.IconPath = cache.CachedPathOrNull(h.IconUrl);
            foreach (var mat in Materials(model)) mat.IconPath = cache.CachedPathOrNull(mat.IconUrl);
            foreach (var skill in model.Skills) skill.IconPath = cache.CachedPathOrNull(skill.IconUrl);
            foreach (var member in Members(model)) member.IconPath = cache.CachedPathOrNull(member.IconUrl);
            foreach (var host in model.TeamHosts) host.IconPath = cache.CachedPathOrNull(host.IconUrl);
            foreach (var step in RotationSteps(model)) step.IconPath = cache.CachedPathOrNull(step.IconUrl);
            foreach (var action in RotationActions(model)) action.IconPath = cache.CachedPathOrNull(action.IconUrl);
            model.SplashPath = cache.CachedPathOrNull(model.SplashUrl);
            model.AmbiencePath = cache.CachedPathOrNull(model.AmbienceUrl);
        }

        /// <returns>True when at least one new icon is now on disk.</returns>
        public static async Task<bool> FetchMissingAsync(CharacterOverviewModel model, CharacterImageCache cache)
        {
            if (model == null || cache == null) return false;
            var jobs = new List<Task<bool>>();
            if (model.IconPath == null && model.IconUrl != null)
                jobs.Add(Fetch(cache, model.IconUrl, p => model.IconPath = p));
            foreach (var set in Sets(model).Where(s => s.IconPath == null && s.IconUrl != null))
                jobs.Add(Fetch(cache, set.IconUrl, p => set.IconPath = p));
            foreach (var weapon in model.Weapons.Where(w => w.IconPath == null && w.IconUrl != null))
                jobs.Add(Fetch(cache, weapon.IconUrl, p => weapon.IconPath = p));
            foreach (var mate in model.TeammateCards.Where(m => m.IconPath == null && m.IconUrl != null))
                jobs.Add(Fetch(cache, mate.IconUrl, p => mate.IconPath = p));
            foreach (var h in model.Highlights.Where(x => x.IconPath == null && x.IconUrl != null))
                jobs.Add(Fetch(cache, h.IconUrl, p => h.IconPath = p));
            foreach (var mat in Materials(model).Where(x => x.IconPath == null && x.IconUrl != null))
                jobs.Add(Fetch(cache, mat.IconUrl, p => mat.IconPath = p));
            foreach (var skill in model.Skills.Where(x => x.IconPath == null && x.IconUrl != null))
                jobs.Add(Fetch(cache, skill.IconUrl, p => skill.IconPath = p));
            foreach (var member in Members(model).Where(x => x.IconPath == null && x.IconUrl != null))
                jobs.Add(Fetch(cache, member.IconUrl, p => member.IconPath = p));
            foreach (var host in model.TeamHosts.Where(x => x.IconPath == null && x.IconUrl != null))
                jobs.Add(Fetch(cache, host.IconUrl, p => host.IconPath = p));
            // A URL repeated across steps and the card above is downloaded once: the cache shares the request in flight.
            foreach (var step in RotationSteps(model).Where(x => x.IconPath == null && x.IconUrl != null))
                jobs.Add(Fetch(cache, step.IconUrl, p => step.IconPath = p));
            foreach (var action in RotationActions(model).Where(x => x.IconPath == null && x.IconUrl != null))
                jobs.Add(Fetch(cache, action.IconUrl, p => action.IconPath = p));
            if (model.SplashPath == null && model.SplashUrl != null)
                jobs.Add(Fetch(cache, model.SplashUrl, p => model.SplashPath = p, CharacterImageCache.LargeMaxBytes));
            if (model.AmbiencePath == null && model.AmbienceUrl != null)
                jobs.Add(Fetch(cache, model.AmbienceUrl, p => model.AmbiencePath = p, CharacterImageCache.LargeMaxBytes));
            if (jobs.Count == 0) return false;
            return (await Task.WhenAll(jobs).ConfigureAwait(false)).Any(got => got);
        }

        private static async Task<bool> Fetch(CharacterImageCache cache, string url, Action<string> assign,
                                              int maxBytes = CharacterImageCache.MaxBytes)
        {
            string path = await cache.GetAsync(url, maxBytes).ConfigureAwait(false);
            if (path == null) return false;
            assign(path);
            return true;
        }

        // Same URL can appear in several groups; each option still gets its own path.
        private static IEnumerable<OverviewSet> Sets(CharacterOverviewModel model) =>
            model.SetGroups.SelectMany(g => g.Options);

        private static IEnumerable<OverviewMaterial> Materials(CharacterOverviewModel model) =>
            model.MaterialsAscension.Concat(model.MaterialsSkills);

        private static IEnumerable<OverviewTeamMember> Members(CharacterOverviewModel model) =>
            model.Teams.SelectMany(t => t.Members);

        private static IEnumerable<OverviewRotationStep> RotationSteps(CharacterOverviewModel model) =>
            model.Teams.SelectMany(t => t.Rotations).SelectMany(r => r.Steps);

        private static IEnumerable<OverviewRotationAction> RotationActions(CharacterOverviewModel model) =>
            RotationSteps(model).SelectMany(s => s.Actions);
    }
}
