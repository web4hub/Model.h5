# Brave Browser Debloating Guide

Brave is an excellent, privacy-focused browser, but its default configuration comes packed with extra features that many users consider bloat. This guide will walk you through how to get rid of the bloat, hide the ads, and disable background services for a clean, minimal browsing experience.

### Part 1: Clearing the Visual Bloat

The default start page and toolbars are crowded with Brave "cards," news feeds, and native crypto shortcuts. Fortunately, the surface-level clutter is easy to hide directly from the UI.

* **Clean the Start Page:** Open a new tab, click the **Customize** button in the bottom right corner, and toggle off *Brave News*, *Cards*, and any unwanted background images or stats.
* **Clean the Toolbar:** Right-click on the Brave Rewards, Brave Wallet, Brave VPN, and Leo AI icons located next to the URL bar, and select **Hide**.

---

### Part 2: Deep Debloating via Policy Management

Hiding icons from the toolbar doesn't actually stop those features from running in the background. To completely disable integrations like the Wallet, VPN, and Leo AI, we need to apply system-level policies.

> **Note:** Applying these policies will permanently disable these features and prevent them from consuming system resources. You will not be able to toggle them back on from the standard settings menu unless you remove the policy files.

**What we are disabling:**

| Feature | Policy Key | Description |
| --- | --- | --- |
| **Brave Rewards** | `BraveRewardsDisabled` | Disables the native ad/crypto reward system. |
| **Brave Wallet** | `BraveWalletDisabled` | Disables the built-in cryptocurrency wallet. |
| **Brave VPN** | `BraveVPNDisabled` | Disables the premium VPN service prompts. |
| **Leo AI Chat** | `BraveAIChatEnabled` | Completely disables the built-in Leo AI assistant. |
| **Brave News** | `BraveNewsDisabled` | Turns off the native news feed aggregator. |
| **Brave Talk** | `BraveTalkDisabled` | Disables the native video conferencing tool. |
| **Sync** | `SyncDisabled` | Disables Brave's bookmark and history syncing. |
| **Tor** | `TorDisabled` | Removes the "Private Window with Tor" feature (often required for enterprise/strict security environments). |

---

### Linux Instructions

To apply these policies on Linux, we need to create a managed policy JSON file.

Open your terminal and create the necessary directory structure:

```bash
sudo mkdir -p /etc/brave/policies/managed/

```

Next, open a new JSON file in your preferred text editor (like nano):

```bash
sudo nano /etc/brave/policies/managed/bloat.json

```

Paste the following JSON array into the file, then save and exit:

```json
{
    "BraveRewardsDisabled": true,
    "BraveWalletDisabled": true,
    "BraveVPNDisabled": true,
    "BraveAIChatEnabled": false,
    "BraveNewsDisabled": true,
    "BraveTalkDisabled": true,
    "SyncDisabled": true,
    "TorDisabled": true,
    "DnsOverHttpsMode": "automatic"
}

```

Brave will apply these policies instantly. Simply open a new window to enjoy your minimal browser.

---

### Windows Instructions

To apply these policies on Windows, we will inject them directly into the Windows Registry.

Open **PowerShell as an Administrator** and execute the following commands block:

```powershell
New-Item -Path "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave" -Force | Out-Null
Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave" -Name "BraveRewardsDisabled" -Type DWord -Value 1
Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave" -Name "BraveNewsDisabled" -Type DWord -Value 1
Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave" -Name "BraveTalkDisabled" -Type DWord -Value 1
Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave" -Name "SyncDisabled" -Type DWord -Value 1
Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave" -Name "BraveWalletDisabled" -Type DWord -Value 1
Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave" -Name "BraveVPNDisabled" -Type DWord -Value 1
Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave" -Name "BraveAIChatEnabled" -Type DWord -Value 0
Set-ItemProperty -Path "HKLM:\SOFTWARE\Policies\BraveSoftware\Brave" -Name "TorDisabled" -Type DWord -Value 1

```

Once the script finishes running, completely restart the Brave browser for the policies to take effect.
