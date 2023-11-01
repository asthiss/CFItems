const sortArrayOfObjects = (arr, propertyName, order = 'asc') => {
    const sortedArr = arr.sort((a, b) => {
        let aProp = a[propertyName];
        aProp = aProp ?? '0';
        let bProp = b[propertyName];
        bProp = bProp ?? '0';
        if(propertyName == 'Weight') {
            aProp = aProp.replace(',', '');
            bProp = bProp.replace(',', '');
        }
        if (+aProp < +bProp) {
            return -1;
        }
        if (+aProp > +bProp) {
            return 1;
        }
        return 0;
    });
    
    if (order === 'desc') {
        return sortedArr.reverse();
    }

    return sortedArr;
};
var items;
document.addEventListener("DOMContentLoaded", () => {
    fetchData();
});

const toggleDisplay = (target) => {
    if(!target) return 
    target.style.display = (target.style.display == 'none') ? 'inline-block' : 'none';
}
// ************************ Data edit ***************** //
function edit(itemId)
{
    
    toggleDisplay(document.getElementById(itemId));
    toggleDisplay(document.getElementById("save"+itemId));
    toggleDisplay(document.getElementById("edit"+itemId));
}

function save(itemId)
{
    let input = document.getElementById(itemId);
    let item = items.filter(item => item.Name === itemId)[0];
    item.Area = input.value;

    saveInAzure(item);

    toggleDisplay(input);
    toggleDisplay(document.getElementById("save"+itemId));
    toggleDisplay(document.getElementById("edit"+itemId));
    document.getElementById("submit").click();
}

// ************************ Data filter ***************** //
let searchForm = document.getElementById("searchForm");
searchForm.addEventListener("submit", (e) => {
    e.preventDefault();
    var itemsToSort =  [...items];

    let type = document.getElementById("item_type").value;
    console.log("type:" + type);
    if(type !== "any")
    {
        itemsToSort = itemsToSort.filter(item => item.Group === type);
    }

    let slot = document.getElementById("slot").value;
    console.log("slot:" + slot);
    if(slot !== "any")
    {
        itemsToSort = itemsToSort.filter(item => item.Type === slot);
    }

    let material = document.getElementById("material").value;
    console.log("material:" + material);
    if(material !== "any")
    {
        itemsToSort = itemsToSort.filter(item => item.Material === material);
    }

    let weapon = document.getElementById("weapon").value;
    console.log("weapon:" + weapon);
    if(weapon !== "any")
    {
        itemsToSort = itemsToSort.filter(item => item.Type === weapon);
    }

    let minAvg = document.getElementById("min_avg").value;
    console.log("minAvg:" + minAvg);
    if(minAvg)
    {
        itemsToSort = itemsToSort.filter(item => +item.Avg > +minAvg);
    }


    let damtype = document.getElementById("damtype").value;
    console.log("damtype:" + damtype);
    if(damtype !== "any")
    {
        itemsToSort = itemsToSort.filter(item => item.BaseDamnoun === damtype);
    }

    let keywords = document.getElementById("keywords").value;
    console.log("keywords:" + keywords);
    if(keywords)
    {
        itemsToSort = itemsToSort.filter(item => item.FullDataPiped.toLowerCase()?.indexOf(keywords.toLowerCase()) !== -1);
    }
    
    let area = document.getElementById("area").value;
    console.log("area:" + area);
    if(area)
    {
        itemsToSort = itemsToSort.filter(item => item.Area === area);
    }

    let affect = document.getElementById("affect").value;
    console.log("affect:" + affect);
    if(affect !== "any")
    {
        itemsToSort = itemsToSort.filter(item => item[affect] !== null);
    }
    
    let flags = document.getElementById("flags").value;
    console.log("flags:" + flags);
    if(flags !== "any")
    {
        itemsToSort = itemsToSort.filter(item => item.FlaggsPiped !== null && item.FlaggsPiped.indexOf(flags) !== -1);
    }
    
    let sortBy = document.getElementById("sortBy").value;
    console.log("sortBy:" + sortBy);
    let sortOrder = document.getElementById("sortOrder").value;
    console.log("sortOrder:" + sortOrder);
   
    createElements(sortArrayOfObjects(itemsToSort, sortBy, sortOrder));
});

// ************************ Data in Azure ***************** //
function fetchData() {
    var xhr = new XMLHttpRequest();
    xhr.open("GET", "https://cfitems.azurewebsites.net/api/items/all");
    xhr.send();
    xhr.responseType = "json";
    xhr.onload = () => {
        if (xhr.readyState == 4 && xhr.status == 200) {
            console.log(xhr.response);
            items=xhr.response;
            createElements(sortArrayOfObjects(xhr.response, "Level", "desc"));
        } else {
            console.log(`Error: ${xhr.status}`);
        }
    };
}

function saveInAzure(item) {
    var xhr = new XMLHttpRequest();
    xhr.open("POST", "https://cfitems.azurewebsites.net/api/items");
    xhr.setRequestHeader("Accept", "application/json");
    xhr.setRequestHeader("Content-Type", "application/json");
    xhr.send(JSON.stringify(item));
    xhr.onreadystatechange = function () {
    if (xhr.readyState === 4) {
        console.log(xhr.status);
        console.log(xhr.responseText);
    }};
}

// ************************ Data showers ***************** //
function template(strings, ...keys) {
    return (...values) => {
        const dict = values[values.length - 1] || {};
        const result = [strings[0]];
        keys.forEach((key, i) => {
            const value = Number.isInteger(key) ? values[key] : dict[key];
            result.push(value, strings[i + 1]);
        });
        return result.join("");
    };
}
    
function createElements(items) {
    var itemTemplate = template`
    <b>Name:</b> ${"Name"} <b>Area:</b> ${"Area"}<input placeholder="Add text for area field here" class="edit" type="text" id="${"Name"}" style="display:none"/><button class="edit" onclick="edit('${"Name"}')" id="edit${"Name"}">edit</button><button style="display:none" class="edit" id="save${"Name"}" onclick="save('${"Name"}')" >save</button> <br>
    <b>Item Type:</b> ${"Group"} <b>Wear:</b> ${"Type"} <b>Material:</b> ${"Material"}<br>	
    <b>Level:</b> ${"Level"} <b>Weight:</b> ${"Kg"}kg ${"Gram"}g <b>Worth:</b> ${"Worth"} copper<br>
    <b>Flags:</b> ${"Flags"}<br>
    ${"ArmorLine"}
    ${"Modifiers"}`;

    var itemTemplateWithOutFlags = template`
    <b>Name:</b> ${"Name"} <b>Area:</b> ${"Area"}<input placeholder="Add text for area field here" class="edit" type="text" id="${"Name"}" style="display:none"/><button class="edit" onclick="edit('${"Name"}')" id="edit${"Name"}">edit</button><button style="display:none" class="edit" id="save${"Name"}" onclick="save('${"Name"}')" >save</button> <br>
    <b>Item Type:</b> ${"Group"} <b>Wear:</b> ${"Type"} <b>Material:</b> ${"Material"}<br>	
    <b>Level:</b> ${"Level"} <b>Weight:</b> ${"Kg"}kg ${"Gram"}g <b>Worth:</b> ${"Worth"} copper<br>
    ${"ArmorLine"}
    ${"Modifiers"}`;

    var weaponTemplate = template`
    <b>Name:</b> ${"Name"} <b>Area:</b> ${"Area"}<input placeholder="Add text for area field here" class="edit" type="text" id="${"Name"}" style="display:none"/><button class="edit" onclick="edit('${"Name"}')" id="edit${"Name"}">edit</button><button style="display:none" class="edit" id="save${"Name"}" onclick="save('${"Name"}')" >save</button> <br>
    <b>Item Type:</b> ${"Group"} <b>Wear:</b> ${"Type"} <b>Material:</b> ${"Material"}<br>	
    <b>Level:</b> ${"Level"} <b>Weight:</b> ${"Kg"}kg ${"Gram"}g <b>Worth:</b> ${"Worth"} copper<br>
    <b>Flags:</b> ${"Flags"}<br>
    <b>Damnoun:</b> ${"Damnoun"}<br>
    <b>Avgerage:</b> ${"Avg"}<br>
    ${"Modifiers"}`;

    var weaponTemplateWithOutFlags = template`
    <b>Name:</b> ${"Name"} <b>Area:</b> ${"Area"}<input placeholder="Add text for area field here" class="edit" type="text" id="${"Name"}" style="display:none"/><button class="edit" onclick="edit('${"Name"}')" id="edit${"Name"}">edit</button><button style="display:none" class="edit" id="save${"Name"}" onclick="save('${"Name"}')" >save</button> <br>
    <b>Item Type:</b> ${"Group"} <b>Wear:</b> ${"Type"} <b>Material:</b> ${"Material"}<br>	
    <b>Level:</b> ${"Level"} <b>Weight:</b> ${"Kg"}kg ${"Gram"}g <b>Worth:</b> ${"Worth"} copper<br>
    <b>Damnoun:</b> ${"Damnoun"}<br>
    <b>Avgerage:</b> ${"Avg"}<br>
    ${"Modifiers"}`;

    var magicTemplate = template`
    <b>Name:</b> ${"Name"} <b>Area:</b> ${"Area"}<input placeholder="Add text for area field here" class="edit" type="text" id="${"Name"}" style="display:none"/><button class="edit" onclick="edit('${"Name"}')" id="edit${"Name"}">edit</button><button style="display:none" class="edit" id="save${"Name"}" onclick="save('${"Name"}')" >save</button> <br>
    <b>Item Type:</b> ${"Group"} <b>Wear:</b> ${"Type"} <b>Material:</b> ${"Material"}<br>	
    <b>Level:</b> ${"Level"} <b>Weight:</b> ${"Kg"}kg ${"Gram"}g <b>Worth:</b> ${"Worth"} copper<br>
    <b>Flags:</b> ${"Flags"}<br>
    ${"MagicAffects"}
    ${"Modifiers"}`;

    var magicTemplateWithOutFlags = template`
    <b>Name:</b> ${"Name"} <b>Area: </b> ${"Area"}<input placeholder="Add text for area field here" class="edit" type="text" id="${"Name"}" style="display:none"/><button class="edit" onclick="edit('${"Name"}')" id="edit${"Name"}">edit</button><button style="display:none" class="edit" id="save${"Name"}" onclick="save('${"Name"}')" >save</button> <br>
    <b>Item Type:</b> ${"Group"} <b>Wear:</b> ${"Type"} <b>Material:</b> ${"Material"}<br>	
    <b>Level:</b> ${"Level"} <b>Weight:</b> ${"Kg"}kg ${"Gram"}g <b>Worth:</b> ${"Worth"} copper<br>
    ${"MagicAffects"}
    ${"Modifiers"}`;

    var parent = document.getElementById("itemList");
    parent.innerHTML = "";
    for (var i = 0; i < items.length; i++) {
        var item = document.createElement('p')
        item.className = 'item ';
        item.className += i%2 == 0 ? 'even' : 'odd';
        var currentItem = items[i];
        if(currentItem.AffectsPiped && !currentItem.Affects.length)
        {    
            currentItem.Affects = currentItem.AffectsPiped.split('|').join('<br>');
        }
        if(currentItem.ModifiersPiped && !currentItem.Modifiers.length)
        {
            currentItem.Modifiers += '<ul>';
            currentItem.ModifiersPiped.split('|').forEach((modifier) => {
                currentItem.Modifiers += '<li>' + modifier + '</li>';
            });
            currentItem.Modifiers += '</ul>';
        }
        if(currentItem.MagicAffectsPiped && !currentItem.MagicAffects.length)
        {
            currentItem.MagicAffectsPiped.split('|').forEach((magicAffect) => {
                currentItem.MagicAffects += magicAffect + '<br>';
            });
        }
        if(currentItem.FlaggsPiped)
        {   
            currentItem.Flags = currentItem.FlaggsPiped.split('|').join(' ');
        }  
        
        if(currentItem.Flags)
        {
            item.innerHTML = itemTemplate(currentItem);
            if(currentItem.IsWeapon)
            {
                item.innerHTML = weaponTemplate(currentItem);
            }
            if(currentItem.IsMagic)
            {
                item.innerHTML = magicTemplate(currentItem);
            }
        }
        else
        {
            item.innerHTML = itemTemplateWithOutFlags(currentItem);
            if(currentItem.IsWeapon)
            {
                item.innerHTML = weaponTemplateWithOutFlags(currentItem);
            }
            if(currentItem.IsMagic)
            {
                item.innerHTML = magicTemplateWithOutFlags(currentItem);
            }
        }

        parent.appendChild(item);
    }
}

// ************************ Drag and drop ***************** //
let dropArea = document.getElementById("drop-area")

    // Prevent default drag behaviors
    ;['dragenter', 'dragover', 'dragleave', 'drop'].forEach(eventName => {
        dropArea.addEventListener(eventName, preventDefaults, false)
        document.body.addEventListener(eventName, preventDefaults, false)
    })

    // Highlight drop area when item is dragged over it
    ;['dragenter', 'dragover'].forEach(eventName => {
        dropArea.addEventListener(eventName, highlight, false)
    })

    ;['dragleave', 'drop'].forEach(eventName => {
        dropArea.addEventListener(eventName, unhighlight, false)
    })

// Handle dropped files
dropArea.addEventListener('drop', handleDrop, false)

function preventDefaults(e) {
    e.preventDefault()
    e.stopPropagation()
}

function highlight(e) {
    dropArea.classList.add('highlight')
}

function unhighlight(e) {
    dropArea.classList.remove('active')
}

function handleDrop(e) {
    var dt = e.dataTransfer
    var files = dt.files

    handleFiles(files)
}

let uploadProgress = []
let progressBar = document.getElementById('progress-bar')

function initializeProgress(numFiles) {
    progressBar.value = 0
    uploadProgress = []

    for (let i = numFiles; i > 0; i--) {
        uploadProgress.push(0)
    }
}

function updateProgress(fileNumber, percent) {
    uploadProgress[fileNumber] = percent
    let total = uploadProgress.reduce((tot, curr) => tot + curr, 0) / uploadProgress.length
    console.debug('update', fileNumber, percent, total)
    progressBar.value = total
}

function handleFiles(files) {
    files = [...files]
    initializeProgress(files.length)
    files.forEach(uploadFile)
    files.forEach(previewFile)
}

function previewFile(file) {
    let reader = new FileReader()
    reader.readAsDataURL(file)
    reader.onloadend = function () {
        let img = document.createElement('img')
        img.src = 'log64.png'
        document.getElementById('gallery').appendChild(img)
    }
}

function uploadFile(file, i) {
    var url = 'https://cfitems.azurewebsites.net/api/LogEater'
    var xhr = new XMLHttpRequest()
    var formData = new FormData()
    xhr.open('POST', url, true)
    xhr.setRequestHeader('X-Requested-With', 'XMLHttpRequest')
    xhr.setRequestHeader('filename', file.name);
    
    // Update progress (can be used to show progress indicator)
    xhr.upload.addEventListener("progress", function (e) {
        updateProgress(i, (e.loaded * 100.0 / e.total) || 100)
    })

    xhr.addEventListener('readystatechange', function (e) {
        if (xhr.readyState == 4 && xhr.status == 200) {
            updateProgress(i, 100) // <- Add this
        }
        else if (xhr.readyState == 4 && xhr.status != 200) {
            // Error. Inform the user
        }
    })

    formData.append('upload_preset', 'ujpu6gyk')
    formData.append('file', file)
    xhr.send(formData)
}