$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$runtime = Join-Path $here '..\TerryMediaHub.SourceCore\Resources\SpiderHost.jar'
$output = Join-Path $here 'out'
$jar = Join-Path $here '..\TerryMediaHub.SourceCore\Resources\AndroidCompat.jar'
New-Item -ItemType Directory -Path $output -Force | Out-Null
& 'C:\Program Files\Java\jdk-21\bin\javac.exe' --release 8 -Xlint:-options -cp $runtime -d $output (Join-Path $here 'android\content\Context.java')
if ($LASTEXITCODE -ne 0) { throw 'javac failed' }
& 'C:\Program Files\Java\jdk-21\bin\jar.exe' cf $jar -C $output .
if ($LASTEXITCODE -ne 0) { throw 'jar failed' }
Write-Output $jar
