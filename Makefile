lean_engine_image ?= quantconnect/lean:latest
docker_command ?= docker
remote_host ?= ubuntu@43.153.118.100
remote_plugin_directory ?= /data/lean-plugins/bin

.PHONY: build sync

build:
	$(docker_command) run --rm \
		--volume "$(CURDIR):/plugin" \
		--workdir /plugin \
		--entrypoint dotnet \
		$(lean_engine_image) \
		build QuantConnect.LeanPlugins.csproj \
			--configuration Release \
			--output /plugin/bin \
			--nologo

sync: build
	rsync -az --delete \
		--rsync-path="sudo rsync" \
		"$(CURDIR)/bin/" \
		"$(remote_host):$(remote_plugin_directory)/"
