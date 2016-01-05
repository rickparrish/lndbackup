using lndapi;
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
        static void Main(string[] args)
        {
            if (args.Length == 5)
            {
                try
                {
                    MainAsync(args).GetAwaiter().GetResult();
                }
                catch (LNDException lndex)
                {
                    Console.WriteLine($"lndapi error: {(Debugger.IsAttached ? lndex.ToString() : lndex.Message)}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"fatal error: {(Debugger.IsAttached ? ex.ToString() : ex.Message)}");
                }

                Console.WriteLine();
                Console.WriteLine("done");
            }
            else
            {
                DisplayUsage();
            }

            if (Debugger.IsAttached) Console.ReadKey();
        }

        static async Task MainAsync(string[] args)
        {
            string Source = args[0];
            string DestinationRegion = args[1];
            string DestinationDirectory = args[2];
            string APIID = args[3];
            string APIKey = args[4];

            // Ensure destination directory exists, or program fails right away if it can't be created
            Directory.CreateDirectory(DestinationDirectory);

            using (LNDynamic client = new LNDynamic(APIID, APIKey))
            {
                List<int> VMIDsToBackup = await GetVMIDsToBackup(client, Source);
                if (VMIDsToBackup.Count == 0)
                {
                    Console.WriteLine("Nothing to backup!");
                }
                else
                {
                    Console.WriteLine($"Found {VMIDsToBackup.Count} VMs to backup");
                    foreach (int VMID in VMIDsToBackup)
                    {
                        try
                        {
                            Console.WriteLine($"- Backing up VMID {VMID}...");
                            var Details = await client.VMInfoAsync(VMID);
                            Console.WriteLine($"  - hostname: {Details.extra.hostname}");
                            Console.WriteLine($"  - region  : {Details.extra.region}");

                            string NewImageName = $"lndbackup {VMID} {DateTime.Now.ToString("yyyy-MM-dd")} {Details.extra.hostname}";
                            string NewImageFilename = Path.Combine(DestinationDirectory, string.Join("_", NewImageName.Split(Path.GetInvalidFileNameChars())) + ".img");

                            int NewImageId = await DoSnapshot(client, VMID, NewImageName);
                            if (Details.extra.region != DestinationRegion) NewImageId = await DoReplicate(client, NewImageId, DestinationRegion);
                            await DoDownload(client, NewImageId, NewImageFilename);
                            // TODO Compress after download using qemu-img? DoCompress();
                            // TODO Delete old images from Luna Node as well as local filesystem DoCleanup();
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

        private static void DisplayUsage()
        {
            Console.WriteLine();
            Console.WriteLine("USAGE");
            Console.WriteLine();
            Console.WriteLine("     lndbackup <src> <dst_region> <dst_dir> <api_id> <api_key>");
            Console.WriteLine();
            Console.WriteLine("     src             vm_id or region name to backup");
            Console.WriteLine("     dst_region      region name to replicate snapshots to");
            Console.WriteLine("     dst_dir         local path to download snapshots to");
            Console.WriteLine("     api_id          API ID from lndynamic control panel");
            Console.WriteLine("     api_key         API Key from lndynamic control panel");
            Console.WriteLine();
            Console.WriteLine("EXAMPLES");
            Console.WriteLine();
            Console.WriteLine("     lndbackup 12345 toronto c:\\lndbackup api_id api_key");
            Console.WriteLine("     Backup VM with id 12345, replicate to Toronto, download to c:\\lndbackup");
            Console.WriteLine("     (If the VM is provisioned in Toronto, no replication occurs)");
            Console.WriteLine();
            Console.WriteLine("     lndbackup toronto roubaix c:\\lndbackup api_id api_key");
            Console.WriteLine("     Backup all Toronto VMs, replicate to Roubaix, download to c:\\lndbackup");
            Console.WriteLine();
            Console.WriteLine("     lndbackup montreal montreal c:\\lndbackup api_id api_key");
            Console.WriteLine("     Backup all Montreal VMs, don't replicate, download to c:\\lndbackup");
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

        private static async Task DoDownload(LNDynamic client, int imageId, string filename)
        {
            // TODO Faster to download from Toronto for me -- maybe have option to specify preferred region to download
            //      from, so in my case it would download from Toronto before replicating to Roubaix?
            Console.WriteLine($"  - Downloading image {imageId} to {filename}...");

            await client.ImageRetrieveAsync(imageId, filename, (s, e) =>
            {
                Console.Write($"\r    - Downloaded {e.BytesReceived:n0} of {e.TotalBytesToReceive:n0} bytes ({e.ProgressPercentage:P})...");
            });
            Console.WriteLine();

            Console.WriteLine("    - Image downloaded successfully!");
            // TODO Validate checksum (here or in lndapi)?
        }

        private static async Task<int> DoReplicate(LNDynamic client, int imageId, string destinationRegion)
        {
            Console.WriteLine($"  - Replicating snapshot image to {destinationRegion}...");

            int ReplicatedImageId = await client.ImageReplicateAndWaitAsync(imageId, destinationRegion, 60, 3, (s, e) =>
            {
                Console.WriteLine($"    - New replication image {e.ImageId} queued for creation!");
            }, (s, e) =>
            {
                Console.WriteLine($"      - Image status is '{e.Status}', waiting {e.WaitSeconds} seconds for 'active'...");
            }, (s, e) =>
            {
                Console.WriteLine($"    - Retry {e.RetryNumber} of {e.MaxRetries}");
            });

            Console.WriteLine("      - Image status is 'active'!");

            // Delete original image, leaving only replicated image
            Console.WriteLine($"  - Removing original snapshot...");
            await client.ImageDeleteAsync(imageId);
            Console.WriteLine($"    - Original snapshot {imageId} deleted!");

            return ReplicatedImageId;
        }

        private static async Task<int> DoSnapshot(LNDynamic client, int vmId, string newImageName)
        {
            Console.WriteLine($"  - Creating snapshot image...");

            int NewImageId = await client.VMSnapshotAndWaitAsync(vmId, newImageName, 60, 3, (s, e) =>
            {
                Console.WriteLine($"    - New snapshot image {e.ImageId} queued for creation!");
            }, (s, e) =>
            {
                Console.WriteLine($"      - Image status is '{e.Status}', waiting {e.WaitSeconds} seconds for 'active'...");
            }, (s, e) =>
            {
                Console.WriteLine($"    - Retry {e.RetryNumber} of {e.MaxRetries}");
            });

            Console.WriteLine("      - Image status is 'active'!");

            return NewImageId;
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
    }
}
