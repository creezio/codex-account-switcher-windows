using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Creezio.Switcher
{
    internal sealed class UsageSettingsForm : Form
    {
        public UsageSettingsForm(RelayStore store,AccountService service)
        {
            Text="Limites et réinitialisations";ClientSize=new Size(930,660);MinimumSize=new Size(720,480);Font=new Font("Segoe UI",10);StartPosition=FormStartPosition.CenterParent;
            var p=UsageCoordinator.Policies(store);var layout=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(18)};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,300));layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));Controls.Add(layout);
            var background=new CheckBox{Text="Continuer la supervision après fermeture du switcher",Checked=p.Background,AutoSize=true};layout.Controls.Add(background);layout.SetColumnSpan(background,2);
            layout.Controls.Add(new Label{Text="Compte",AutoSize=true});var account=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};foreach(var profile in service.Data.Profiles)account.Items.Add(new AccountChoice(profile,profile.Label));layout.Controls.Add(account);
            layout.Controls.Add(new Label{Text="Utilisation des réinitialisations",AutoSize=true});var mode=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};mode.Items.AddRange(new[]{"Selon le réglage global","Automatique","Manuelle","Désactivée"});layout.Controls.Add(mode);
            layout.Controls.Add(new Label{Text="Seuil exact (% restant)",AutoSize=true});var threshold=new NumericUpDown{Minimum=0,Maximum=25,DecimalPlaces=1,Value=1};layout.Controls.Add(threshold);
            layout.Controls.Add(new Label{Text="Fenêtre déclenchant le reset",AutoSize=true});var window=new ComboBox{Dock=DockStyle.Top,DropDownStyle=ComboBoxStyle.DropDownList};window.Items.AddRange(new[]{"Toutes les fenêtres Codex","5 heures","Hebdomadaire"});layout.Controls.Add(window);
            var notice=new Label{AutoSize=true,MaximumSize=new Size(850,0),Text="Le choix de fenêtre contrôle le déclenchement. Le crédit disponible peut rétablir plusieurs fenêtres : le serveur décide de sa portée. Aucun crédit acheté n'est utilisé."};layout.Controls.Add(notice);layout.SetColumnSpan(notice,2);
            var notify=new CheckBox{Text="Notifications pour ce compte",AutoSize=true};layout.Controls.Add(notify);layout.SetColumnSpan(notify,2);
            var history=new TextBox{Dock=DockStyle.Fill,ReadOnly=true,Multiline=true,ScrollBars=ScrollBars.Vertical};Controls.Add(history);history.BringToFront();
            AccountUsagePolicy selected=null;
            Action saveDraft=delegate{if(selected==null)return;selected.Mode=new[]{"inherit","auto","manual","off"}[Math.Max(0,mode.SelectedIndex)];selected.Threshold=(double)threshold.Value;selected.Window=new[]{"all","session","weekly"}[Math.Max(0,window.SelectedIndex)];selected.Notifications=notify.Checked;};
            account.SelectedIndexChanged+=delegate{saveDraft();var key=((AccountChoice)account.SelectedItem).Profile.Key;selected=p.Accounts.FirstOrDefault(x=>x.Account==key);if(selected==null){selected=new AccountUsagePolicy{Account=key};p.Accounts.Add(selected);}mode.SelectedIndex=Array.IndexOf(new[]{"inherit","auto","manual","off"},selected.Mode);threshold.Value=(decimal)selected.Threshold;window.SelectedIndex=Array.IndexOf(new[]{"all","session","weekly"},selected.Window);notify.Checked=selected.Notifications;var h=store.ReadRecord<UsageHistory>("reset-history-"+RelayReturns.Key(key)+".dpapi");history.Text="Historique des réinitialisations\r\n\r\n"+String.Join("\r\n\r\n",h.Events.AsEnumerable().Reverse().Select(x=>x.Time+" · "+x.State+" · "+ProductUx.Percent(x.Remaining)+"\r\n"+x.Message));};
            var save=new Button{Text="Enregistrer la politique",Dock=DockStyle.Bottom,Height=42};Controls.Add(save);save.Click+=delegate{try{saveDraft();p.Background=background.Checked;UsageCoordinator.SavePolicies(store,p);if(p.Background)UsageCoordinator.Ensure(store);DialogResult=DialogResult.OK;Close();}catch(Exception e){MessageBox.Show(this,Program.SafeError(e));}};
            var reset=new Button{Text="Utiliser une réinitialisation maintenant…",Dock=DockStyle.Bottom,Height=38};Controls.Add(reset);
            reset.Click+=async delegate{
                if(account.SelectedItem==null)return;string key=((AccountChoice)account.SelectedItem).Profile.Key;
                if(MessageBox.Show(this,"Consommer une réinitialisation de limites disponible pour le compte sélectionné ? Cette action est immédiate, même au-dessus du seuil automatique. Le serveur détermine les fenêtres rétablies.","Réinitialisation manuelle",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
                reset.Enabled=false;try{saveDraft();UsageCoordinator.SavePolicies(store,p);if(UsageCoordinator.LegacyGuiRunning())throw new InvalidOperationException("Quittez l'ancien switcher avant de consommer une réinitialisation.");await UsageCoordinator.Refresh(store,key,true,System.Threading.CancellationToken.None,true);var result=store.ReadRecord<Profile>(RelayQuota.Name(key));MessageBox.Show(this,result.ResetMessage??"Aucune réinitialisation disponible ou politique désactivée.");}catch(Exception e){MessageBox.Show(this,Program.SafeError(e));}finally{if(!IsDisposed)reset.Enabled=true;}
            };
            if(account.Items.Count>0)account.SelectedIndex=0;ProductUx.Accessible(this);
        }
    }
}
