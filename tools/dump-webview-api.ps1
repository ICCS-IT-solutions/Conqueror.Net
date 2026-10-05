$dll = 'C:\Users\Iain\vscode\cs\Conqueror.Net\bin\Debug\net8.0\WebViewControl.Avalonia.dll'
$asm = [System.Reflection.Assembly]::LoadFrom($dll)

foreach ($t in $asm.GetExportedTypes() | Sort-Object FullName) {
    Write-Output "=== $($t.FullName) === (base: $($t.BaseType?.Name))"
    foreach ($m in $t.GetMembers([System.Reflection.BindingFlags]'Public,Instance,Static,DeclaredOnly') | Sort-Object MemberType, Name) {
        switch ($m.MemberType) {
            'Property' { Write-Output ("  PROP  {0} {1}" -f $m.PropertyType.Name, $m.Name) }
            'Event'   { Write-Output ("  EVENT {0} {1}" -f $m.EventHandlerType.Name, $m.Name) }
            'Method'  {
                if ($m.Name -notmatch '^(get_|set_|add_|remove_|op_)') {
                    $ps = ($m.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
                    Write-Output ("  METH  {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $ps)
                }
            }
        }
    }
}
