using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Creezio.Switcher.Desktop
{
    internal sealed class AccountLoginPanel : StackPanel
    {
        private readonly DesktopContext context;
        private readonly ComboBox browser,profile;
        private readonly StackPanel selection=new StackPanel(),profileFields=new StackPanel(),linkFields=new StackPanel();
        private readonly TextBlock hint=Ui.Text("",13,true),status=Ui.Text("",13,true);
        private readonly TextBox link;
        private readonly Button copy,cancel;
        private CancellationTokenSource operation;
        internal Action<string> CopyText=Clipboard.SetText;

        internal AccountLoginPanel(DesktopContext context,Settings settings,LoginBrowser[] browsers)
        {
            this.context=context;
            Children.Add(selection);
            selection.Children.Add(Ui.Text("Navigateur pour la connexion",13));
            browser=new ComboBox{ItemsSource=browsers,DisplayMemberPath="Label"};
            AutomationProperties.SetName(browser,"Navigateur pour la connexion");selection.Children.Add(browser);
            selection.Children.Add(profileFields);profileFields.Children.Add(Ui.Text("Profil du navigateur",13));
            profile=new ComboBox{DisplayMemberPath="Label"};AutomationProperties.SetName(profile,"Profil du navigateur");profileFields.Children.Add(profile);
            selection.Children.Add(hint);
            browser.SelectionChanged+=delegate { UpdateProfiles(); };
            var saved=browsers.FirstOrDefault(b=>b.Id==settings.LoginBrowserId);
            browser.SelectedItem=saved??browsers.First(b=>b.Id==(String.IsNullOrEmpty(settings.LoginBrowserId)?"system":"manual"));
            if(saved!=null&&!String.IsNullOrEmpty(settings.LoginBrowserProfile)) {
                profile.SelectedItem=saved.Profiles.FirstOrDefault(p=>p.Directory==settings.LoginBrowserProfile);
                if(profile.SelectedItem==null)hint.Text="Le profil utilisé précédemment est introuvable. Choisissez un profil ou copiez le lien.";
            } else if(saved==null&&!String.IsNullOrEmpty(settings.LoginBrowserId)) hint.Text="Le navigateur utilisé précédemment est introuvable. Choisissez-en un autre ou copiez le lien.";
            Children.Add(linkFields);linkFields.Visibility=Visibility.Collapsed;
            linkFields.Children.Add(Ui.Text("Lien de connexion",13));
            link=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=60,MaxHeight=100,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            AutomationProperties.SetName(link,"Lien de connexion");linkFields.Children.Add(link);
            copy=Ui.Button("Copier le lien",()=>{try{CopyText(link.Text);status.Text="Lien copié. Collez-le dans le navigateur et le profil souhaités sur ce PC.";}catch{status.Text="Le presse-papiers est occupé. Sélectionnez le lien pour le copier manuellement.";}});
            linkFields.Children.Add(copy);
            Children.Add(status);
            cancel=Ui.Button("Annuler la connexion",()=>operation?.Cancel());cancel.Visibility=Visibility.Collapsed;Children.Add(cancel);
        }
        private void UpdateProfiles()
        {
            var selected=browser.SelectedItem as LoginBrowser;
            profile.ItemsSource=selected?.Profiles;profile.SelectedIndex=selected?.Profiles.Length>0?0:-1;
            profileFields.Visibility=selected!=null&&selected.Profiles.Length>0?Visibility.Visible:Visibility.Collapsed;
            hint.Text=selected?.Id=="manual"?"Le lien s’affichera au démarrage de la connexion. Collez-le dans le navigateur de votre choix sur ce PC."
                :selected?.Id=="system"?"Windows choisira le navigateur et sa session. Sélectionnez un navigateur ci-dessus pour choisir un profil."
                :selected?.Profiles.Length>0?"Le compte ChatGPT connecté dans ce profil sera proposé. Vous pourrez le vérifier avant de valider."
                :"Aucun profil détecté : le navigateur choisira sa session. Vous pouvez aussi choisir « Copier le lien ».";
        }
        internal async Task<string> Connect(Func<CancellationToken,Task<string>> legacyFixture=null)
        {
            if(operation!=null)throw new InvalidOperationException("Une connexion est déjà en cours.");
            var selected=browser.SelectedItem as LoginBrowser;
            var selectedProfile=profile.SelectedItem as LoginBrowserProfile;
            if(selected==null || (selected.Profiles.Length>0&&selectedProfile==null))throw new InvalidOperationException("Choisissez le navigateur et son profil pour la connexion.");
            // Capture every control on the dispatcher before starting async work.
            var target=new LoginBrowserTarget(selected,selectedProfile?.Directory);
            operation=new CancellationTokenSource(TimeSpan.FromMinutes(5));var token=operation.Token;
            selection.IsEnabled=false;link.Text="";linkFields.Visibility=Visibility.Collapsed;cancel.Visibility=Visibility.Visible;status.Text="Préparation du lien de connexion…";
            try {
                await context.Mutate(a=>{a.Settings.LoginBrowserId=target.Id;a.Settings.LoginBrowserProfile=target.ProfileDirectory;a.Vault.SaveSettings(a.Settings);return Task.CompletedTask;});
                var service=await context.Read(a=>a);
                Action<string> present=url=>Dispatcher.Invoke(()=>{
                    LoginBrowserTarget.ValidateUrl(url);
                    if(token.IsCancellationRequested)return;
                    link.Text=url;linkFields.Visibility=Visibility.Visible;
                    status.Text="Terminez la connexion dans votre navigateur. Ce lien est utilisable pendant cette tentative (5 minutes maximum).";
                    _=Dispatcher.BeginInvoke(new Action(()=>{if(linkFields.IsVisible)cancel.BringIntoView();}),System.Windows.Threading.DispatcherPriority.Loaded);
                    try {
                        if(context.Fixture) { if(context.LoginOpenFixture!=null)context.LoginOpenFixture(target,url); }
                        else target.Open(url);
                    } catch { status.Text="L’ouverture du navigateur a échoué. Copiez le lien et ouvrez-le dans le profil souhaité sur ce PC, ou annulez pour changer de navigateur."; }
                });
                string auth=context.Fixture
                    ? await (context.LoginAuthFixture!=null?context.LoginAuthFixture(present,token):legacyFixture?.Invoke(token)??Task.FromException<string>(new InvalidOperationException("Connexion non configurée dans cette recette.")))
                    : await service.LoginAuth(present,token);
                token.ThrowIfCancellationRequested();
                status.Text="Connexion terminée.";return auth;
            } catch(OperationCanceledException) {status.Text="Connexion annulée ou expirée. Relancez-la pour obtenir un nouveau lien.";throw new InvalidOperationException(status.Text);}
            finally {
                // The authorization URL is transient, never part of settings or diagnostics.
                link.Text="";linkFields.Visibility=Visibility.Collapsed;cancel.Visibility=Visibility.Collapsed;selection.IsEnabled=true;
                operation.Dispose();operation=null;
            }
        }
        internal static Task<LoginBrowser[]> Catalog(DesktopContext context)
            =>context.Fixture?Task.FromResult(context.LoginBrowsersFixture??LoginBrowsers.Basic()):Task.Run(LoginBrowsers.Discover);
        internal static async Task<string> Open(DesktopContext context,Window owner)
        {
            var settings=await context.Read(a=>a.Settings);var browsers=await Catalog(context);
            var form=new EditWindow(owner,"Connecter un compte","Démarrer la connexion");
            var panel=new AccountLoginPanel(context,settings,browsers);form.Fields.Children.Add(panel);
            string auth=null;form.Save=async()=>{auth=await panel.Connect();};form.ShowDialog();
            return form.Saved?auth:null;
        }
    }
}
