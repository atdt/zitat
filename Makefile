.PHONY: build test run run-corpus publish clean format format-check

build:
	dotnet build Zitat.slnx

test:
	dotnet test Zitat.slnx

run:
	dotnet run --project src/Zitat/Zitat.fsproj

run-corpus:
	@test -d "$(CURDIR)/testdata/journal" || { echo "testdata/journal is missing" >&2; exit 1; }
	@echo "Web UI: http://127.0.0.1:5080/"
	ASPNETCORE_URLS=http://127.0.0.1:5080 \
		Zitat__JournalDirectory="$(CURDIR)/testdata/journal" \
		dotnet run --project src/Zitat/Zitat.fsproj

publish:
	./scripts/publish-linux-arm64.sh

clean:
	dotnet clean Zitat.slnx

format:
	dotnet fantomas src tests

format-check:
	dotnet fantomas --check src tests
