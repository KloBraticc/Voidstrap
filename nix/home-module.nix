self:
{
  config,
  lib,
  pkgs,
  ...
}:

let
  cfg = config.programs.voidstrap;
  desktopEntry = "io.github.KloBraticc.Voidstrap.desktop";
  schemes = [
    "x-scheme-handler/roblox"
    "x-scheme-handler/roblox-player"
    "x-scheme-handler/roblox-studio"
    "x-scheme-handler/roblox-studio-auth"
    "x-scheme-handler/voidstrap"
  ];
in
{
  options.programs.voidstrap = {
    enable = lib.mkEnableOption "Voidstrap, a Roblox bootstrapper that launches Roblox through Sober";

    package = lib.mkOption {
      type = lib.types.package;
      default = self.packages.${pkgs.stdenv.hostPlatform.system}.voidstrap;
      defaultText = lib.literalExpression "voidstrap.packages.\${pkgs.stdenv.hostPlatform.system}.voidstrap";
      description = "The Voidstrap package to install.";
    };

    openRobloxLinks = lib.mkOption {
      type = lib.types.bool;
      default = false;
      description = "Open roblox:// and voidstrap:// links from the browser with Voidstrap through a Home Manager managed mimeapps.list. Voidstrap registers these links itself when this is off.";
    };
  };

  config = lib.mkIf cfg.enable {
    home.packages = [ cfg.package ];

    xdg.mimeApps = lib.mkIf cfg.openRobloxLinks {
      enable = lib.mkDefault true;
      defaultApplications = lib.genAttrs schemes (_: desktopEntry);
    };
  };
}
