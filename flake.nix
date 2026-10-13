{
  description = "Voidstrap, a Roblox bootstrapper that launches Roblox through Sober on Linux";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs =
    { self, nixpkgs }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];
      forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
      version = builtins.head (
        builtins.elemAt (builtins.split "<VoidstrapVersion>([0-9.]+)</VoidstrapVersion>" (builtins.readFile ./Directory.Build.props)) 1
      );
    in
    {
      packages = forAllSystems (pkgs: rec {
        voidstrap = pkgs.callPackage ./nix/package.nix { };
        voidstrap-git = voidstrap.overrideAttrs {
          inherit version;
          src = self;
        };
        default = voidstrap;
      });

      overlays.default = final: prev: {
        voidstrap = self.packages.${final.stdenv.hostPlatform.system}.voidstrap;
      };

      devShells = forAllSystems (pkgs: {
        default = pkgs.mkShell {
          inputsFrom = [ self.packages.${pkgs.stdenv.hostPlatform.system}.voidstrap-git ];
          packages = [
            pkgs.cargo
            pkgs.rustc
          ];
        };
      });

      formatter = forAllSystems (pkgs: pkgs.nixfmt);
    };
}
