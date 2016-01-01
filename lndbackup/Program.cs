﻿using lndapi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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
                                try
                                {
                                    await HandleBackup(client, VMID, args[3]);
                                }
                                catch (LNDException lndex)
                                {
                                    Console.WriteLine($"lndapi error: {(Debugger.IsAttached ? lndex.ToString() : lndex.Message)}");
                                    Console.WriteLine();
                                    Console.WriteLine("  - Moving on to next VM to backup...");
                                    Console.WriteLine();
                                    Console.WriteLine();
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"fatal error: {(Debugger.IsAttached ? ex.ToString() : ex.Message)}");
                                    Console.WriteLine();
                                    Console.WriteLine("  - Moving on to next VM to backup...");
                                    Console.WriteLine();
                                    Console.WriteLine();
                                }
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

        private static async Task HandleBackup(LNDynamic client, int vmId, string destinationRegion)
        {
            Console.WriteLine($"- Backing up VMID {vmId}...");
            var Details = await client.VMInfoAsync(vmId);
            Console.WriteLine($"  - hostname: {Details.extra.hostname}");
            Console.WriteLine($"  - region  : {Details.extra.region}");

            // Take a snapshot of the VM
            Console.WriteLine($"  - Creating snapshot...");
            string NewImageName = $"lndbackup {vmId} {DateTime.Now.ToString("yyyy-MM-dd")} {Details.extra.hostname}";
            int NewImageId = await client.VMSnapshotAsync(vmId, NewImageName);
            Console.WriteLine($"    - New image {NewImageId} queued for creation!");

            // Wait for new image to be 'active'
            string NewImageStatus = (await client.ImageDetailsAsync(NewImageId)).status;
            while (NewImageStatus != "active")
            {
                Console.WriteLine($"      - Image status is '{NewImageStatus}', waiting 30 seconds for 'active'...");
                Thread.Sleep(30000);
                NewImageStatus = (await client.ImageDetailsAsync(NewImageId)).status;
            }
            Console.WriteLine("      - Image status is 'active'!");

            // Replicate image to new region (if necessary)
            if (Details.extra.region != destinationRegion)
            {
                // Replicate the image to the new region
                Console.WriteLine($"  - Replicating image...");
                int ReplicatedImageId = await client.ImageReplicateAsync(NewImageId, destinationRegion);
                Console.WriteLine($"    - New image {ReplicatedImageId} queued for replication!");

                // Wait for new image to be 'active'
                string ReplicatedImageStatus = (await client.ImageDetailsAsync(ReplicatedImageId)).status;
                while (ReplicatedImageStatus != "active")
                {
                    // TODO One one run status was 'killed'.  Do we abort, or delete and retry replication, or ?
                    //      Maybe instead of looking for 'killed', look for NOT 'queued' or 'saving'?
                    //      There's the snapshot watch above too (maybe this duplicate logic should be extracted to its own method)
                    Console.WriteLine($"      - Image status is '{ReplicatedImageStatus}', waiting 30 seconds for 'active'...");
                    Thread.Sleep(30000);
                    ReplicatedImageStatus = (await client.ImageDetailsAsync(ReplicatedImageId)).status;
                }
                Console.WriteLine("      - Image status is 'active'!");

                // Delete "new" image, leaving only replicated image
                Console.WriteLine($"  - Removing old image...");
                await client.ImageDeleteAsync(NewImageId);
                Console.WriteLine($"    - Old image {NewImageId} deleted!");

                NewImageId = ReplicatedImageId;
            }

            // Download new image
            string ImagesDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
            string CleanFilename = string.Join("_", NewImageName.Split(Path.GetInvalidFileNameChars())) + ".img";
            Console.WriteLine($"  - Downloading image {NewImageId} to {CleanFilename}...");
            Directory.CreateDirectory(ImagesDirectory);
            await client.ImageRetrieveAsync(NewImageId, Path.Combine(ImagesDirectory, CleanFilename), (s, e) =>
            {
                Console.Write($"\r    - Downloaded {e.BytesReceived:n0} of {e.TotalBytesToReceive:n0} ({e.ProgressPercentage:P})...");
            });
            Console.WriteLine();
            Console.WriteLine("    - Image downloaded successfully!");

            // TODO Use ImageList to find old lndbackup generated images for this VM and delete them

            Console.WriteLine();
            Console.WriteLine();
        }
    }
}
