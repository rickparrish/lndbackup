using lndapi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace lndbackup
{
    class Program
    {
        private static object _ImageRetrievedProgressChangedLock = new object();

        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        static async Task MainAsync(string[] args)
        {
            if (args.Length == 4)
            {
                try
                {
                    using (LNDynamic client = new LNDynamic(args[0], args[1]))
                    {
                        List<int> VMIDsToBackup = await GetVMIDsToBackup(client, args[2]);
                        if (VMIDsToBackup.Count == 0)
                        {
                            Console.WriteLine("Nothing to backup!");
                        }
                        else
                        {
                            foreach (int VMID in VMIDsToBackup)
                            {
                                await HandleBackup(client, VMID, args[3]);
                            }
                        }
                    }
                }
                catch (LNDException lndex)
                {
                    Console.WriteLine($"lndapi error: {(Debugger.IsAttached ? lndex.ToString() : lndex.Message)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"fatal error: {(Debugger.IsAttached ? ex.ToString() : ex.Message)}");
                }
            }
            else
            {
                Console.WriteLine("USAGE: lndbackup <api_id> <api_key> <source> <destination>");
                Console.WriteLine();
                Console.WriteLine("     api_id           API ID from lndynamic control panel");
                Console.WriteLine("     api_key          API Key from lndynamic control panel");
                Console.WriteLine("     source           vm_id or region name to backup");
                Console.WriteLine("     destination      region name to replicate snapshots to");
                Console.WriteLine();
                Console.WriteLine("EXAMPLES");
                Console.WriteLine();
                Console.WriteLine("     lndbackup api_id api_key 12345 toronto");
                Console.WriteLine("     Backup VM with id 12345 and replicate the snapshot to Toronto");
                Console.WriteLine("     (If the VM is provisioned in Toronto, no replication occurs)");
                Console.WriteLine();
                Console.WriteLine("     lndbackup api_id api_key toronto roubaix");
                Console.WriteLine("     Backup all VMs in Toronto and replicate the snapshots to Roubaix");
                Console.WriteLine();
                Console.WriteLine("     lndbackup api_id api_key montreal montreal");
                Console.WriteLine("     Backup all VMs in Montreal, and don't replicate the snapshots");
                Console.WriteLine();
                Console.WriteLine("VM_IDs");
                Console.WriteLine();
                Console.WriteLine("     You can find the vm_id for a VM by loading it from the lndynamic control");
                Console.WriteLine("     panel and looking at the querystring.  For example, this is vm_id 12345:");
                Console.WriteLine("     https://dynamic.lunanode.com/panel/vm.php?vm_id=12345");
                Console.WriteLine();
                Console.WriteLine("REGIONS");
                Console.WriteLine();
                Console.WriteLine("     Currently available regions are toronto, montreal, and roubaix");
            }

            Console.WriteLine();
            Console.WriteLine("done");

            if (Debugger.IsAttached) Console.ReadKey();
        }

        private static async Task<List<int>> GetVMIDsToBackup(LNDynamic client, string source)
        {
            List<int> Result = new List<int>();

            if (source.All(Char.IsDigit))
            {
                // Backing up a single vm_id
                Result.Add(int.Parse(source));
            }
            else
            {
                // Backing up an entire region
                var VMs = await client.VMListAsync();
                foreach (var VM in VMs.OrderBy(x => x.vm_id))
                {
                    if (VM.region == source) Result.Add(VM.vm_id);
                }
            }

            return Result;
        }

        private static async Task HandleBackup(LNDynamic client, int vmId, string destination)
        {
            Console.WriteLine($"Backing up VMID {vmId}");
            var Details = await client.VMInfoAsync(vmId);
            Console.WriteLine($"  hostname: {Details.extra.hostname}");
            Console.WriteLine($"  region  : {Details.extra.region}");

            // TODO Take a snapshot (what filename format to use?)
            // TODO Monitor image to see when snapshot has completed
            // TODO Replicate image to new region (if necessary)
            // TODO Delete original image
            // TODO Delete other lndbackup generated images for this VM
            // TODO Download image (what filename format to use?)        
        }
    }
}
