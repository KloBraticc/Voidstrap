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
      default = true;
      description = "Open roblox:// and voidstrap:// links from the browser with Voidstrap.";
    };
  };

  config = lib.mkIf cfg.enable {
    environment.systemPackages = [ cfg.package ];

    hardware.graphics.enable = lib.mkDefault true;

    services.flatpak.enable = lib.mkDefault true;

    xdg.portal = lib.mkIf config.services.flatpak.enable {
      enable = lib.mkDefault true;
      extraPortals = lib.mkDefault [ pkgs.xdg-desktop-portal-gtk ];
      config.common.default = lib.mkDefault "*";
    };

    xdg.mime.defaultApplications = lib.mkIf cfg.openRobloxLinks (lib.genAttrs schemes (_: desktopEntry));
  };
}
