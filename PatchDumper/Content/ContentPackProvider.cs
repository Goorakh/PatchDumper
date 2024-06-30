using RoR2.ContentManagement;
using System.Collections;

namespace PatchDumper.Content
{
    internal class ContentPackProvider : IContentPackProvider
    {
        readonly ContentPack _contentPack = new ContentPack();

        public string identifier => PatchDumperPlugin.PluginGUID;

        internal ContentPackProvider()
        {
        }

        internal void Register()
        {
            ContentManager.collectContentPackProviders += addContentPackProvider =>
            {
                addContentPackProvider(this);
            };
        }

        public IEnumerator LoadStaticContentAsync(LoadStaticContentAsyncArgs args)
        {
#pragma warning disable Publicizer001 // Accessing a member that was not originally public
            _contentPack.identifier = identifier;
#pragma warning restore Publicizer001 // Accessing a member that was not originally public

            // _contentPack.itemDefs.Add(...)

            args.ReportProgress(1f);
            yield break;
        }

        public IEnumerator GenerateContentPackAsync(GetContentPackAsyncArgs args)
        {
            ContentPack.Copy(_contentPack, args.output);
            args.ReportProgress(1f);
            yield break;
        }

        public IEnumerator FinalizeAsync(FinalizeAsyncArgs args)
        {
            args.ReportProgress(1f);
            yield break;
        }
    }
}
