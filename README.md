# lndbackup
Backup your Luna Node VMs using the Luna Node Dynamic API via the <a href="https://github.com/rickparrish/lndapi">lndapi</a> wrapper

# USAGE

Step 1) Create some VMs at <a href="https://dynamic.lunanode.com/info.php?r=2427">Luna Node</a> (aff. link)<br />
Step 2) Create an API key** at https://dynamic.lunanode.com/panel/api.php<br />
Step 3) Use lndbackup to backup your VMs (lndbackup also needs <a href="https://github.com/rickparrish/lndapi">lndapi</a>)<br />

Additional usage information can be found by running lndbackup without any command-line parameters.

** For added security you can set the access restrictions to <strong>vm.list, vm.info, vm.snapshot, image.details, image.replicate, image.delete, image.retrieve</strong>, since that's all that lndbackup needs.
