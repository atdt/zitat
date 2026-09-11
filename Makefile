.PHONY: build test run publish clean

build:
	dotnet build Zitat.slnx

test:
	dotnet test Zitat.slnx

run:
	dotnet run --project src/Zitat/Zitat.fsproj

publish:
	./scripts/publish-linux-arm64.sh

clean:
	dotnet clean Zitat.slnx
