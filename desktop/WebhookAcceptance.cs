using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace Creezio.Switcher.Desktop
{
    internal static partial class DesktopTests
    {
        internal static int RunWebhookUi(Application app,string root)
        {
            Directory.CreateDirectory(root);var report=new List<string>();int count=0;
            try{
                WebhookUiTests.RunAll((name,test)=>{test();count++;report.Add("PASS "+name);});
                report.Add(count+" webhook UI tests passed.");File.WriteAllLines(Path.Combine(root,"webhook-ui-tests.txt"),report);return 0;
            }catch(Exception error){report.Add("FAIL "+error);File.WriteAllLines(Path.Combine(root,"webhook-ui-tests.txt"),report);return 1;}
        }
    }
}
