.PHONY: build test run publish clean format format-check

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

format:
	dotnet fantomas src tests

format-check:
	dotnet fantomas --check src tests
