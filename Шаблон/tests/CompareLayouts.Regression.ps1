param(
    [Parameter(Mandatory=$true)][string]$BuildDirectory,
    [Parameter(Mandatory=$true)][string]$BaselineJson,
    [Parameter(Mandatory=$true)][string]$CandidateJson
)
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $BuildDirectory 'Newtonsoft.Json.dll')
$before=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText((Resolve-Path -LiteralPath $BaselineJson)))
$after=[Newtonsoft.Json.Linq.JObject]::Parse([IO.File]::ReadAllText((Resolve-Path -LiteralPath $CandidateJson)))
foreach($doc in @($before,$after)) {
    $null=$doc.Remove('CreatedUtc')
    $groups=@{}
    foreach($zone in $doc['Zones']) {
        $id=[string]$zone['SpliceGroupId']
        if($id) {
            if(!$groups.ContainsKey($id)) {$groups[$id]='group'+$groups.Count}
            $zone['SpliceGroupId']=[Newtonsoft.Json.Linq.JValue]::new($groups[$id])
        }
    }
}
if(![Newtonsoft.Json.Linq.JToken]::DeepEquals($before,$after)) {
    $different=@($before.Properties()|Where-Object {
        ![Newtonsoft.Json.Linq.JToken]::DeepEquals($before[$_.Name],$after[$_.Name])
    }|ForEach-Object {$_.Name})
    throw ('Layout changed: '+($different -join ', '))
}
Write-Host ('PASS identical layout, FE/As, settings, patches, steel specification and diagnostics; zones='+$after['Zones'].Count)
