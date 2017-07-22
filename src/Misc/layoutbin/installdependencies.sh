#!/bin/bash

user_id=`id -u`

if [ $user_id -ne 0 ]; then
    echo "Need to run with sudo privilege"
    exit 1
fi

# Determine OS type 
# Debian based OS (Debian, Ubuntu, Linux Mint) has /etc/debian_version
# Fedora based OS (Fedora, Redhat, Centos) has /etc/redhat-release
# SUSE based OS (OpenSUSE, SUSE Enterprise) has ID_LIKE=suse in /etc/os-release

function print_errormessage() 
{
    echo "Can't install dotnet core dependencies."
    echo "You can manually install all required dependencies base on follwoing documentation"
    echo "https://docs.microsoft.com/en-us/dotnet/core/linux-prerequisites?tabs=netcore2x"
}

if [ -e /etc/os-release ]
then
    echo "--------OS Information--------"
    cat /etc/os-release
    echo "------------------------------"

    if [ -e /etc/debian_version ]
    then
        echo "The current OS is Debian based"
        echo "--------Debian Version--------"
        cat /etc/debian_version
        echo "------------------------------"
        
        # prefer apt over apt-get
        command -v apt
        if [ $? -eq 0 ]
        then
            apt update && apt install -y libunwind8-dev gettext libicu-dev liblttng-ust-dev libcurl4-openssl-dev libssl-dev uuid-dev unzip
            if [ $? -ne 0 ]
            then
                echo "'apt' failed with exit code '$?'"
                print_errormessage
                exit 1
            fi
        else
            command -v apt-get
            if [ $? -eq 0 ]
            then
                apt-get update && apt-get install -y libunwind8-dev gettext libicu-dev liblttng-ust-dev libcurl4-openssl-dev libssl-dev uuid-dev unzip
                if [ $? -ne 0 ]
                then
                    echo "'apt-get' failed with exit code '$?'"
                    print_errormessage
                    exit 1
                fi
            else
                echo "Can not find 'apt' or 'apt-get'"
                print_errormessage
                exit 1
            fi
        fi
    elif [ -e /etc/redhat-release ]
    then
        echo "The current OS is Fedora based"
        echo "--------Redhat Version--------"
        cat /etc/redhat-release
        echo "------------------------------"

        # use dnf on fedora
        # use yum on centos and redhat
        if [ -e /etc/fedora-release ]
        then
            command -v dnf
            if [ $? -eq 0 ]
            then
                grep -i 'fedora release 26' /etc/fedora-release
                if [ $? -eq 0 ]
                then
                    echo "Use compat-openssl10-devel instead of openssl-devel for Fedora 26 (dotnet core requires openssl 1.0.x)"
                    
                    # epel-release doesn't exists in fedora's package repository, so we can't use dnf to install it
                    rpm -Uvh https://dl.fedoraproject.org/pub/epel/epel-release-latest-7.noarch.rpm && dnf install -y deltarpm unzip libunwind gettext libcurl-devel compat-openssl10 zlib libicu-devel
                    if [ $? -ne 0 ]
                    then
                        echo "'dnf' failed with exit code '$?'"
                        print_errormessage
                        exit 1
                    fi
                else
                    # epel-release doesn't exists in fedora's package repository, so we can't use dnf to install it
                    rpm -Uvh https://dl.fedoraproject.org/pub/epel/epel-release-latest-7.noarch.rpm && dnf install -y deltarpm unzip libunwind gettext libcurl-devel openssl-devel zlib libicu-devel
                    if [ $? -ne 0 ]
                    then
                        echo "'dnf' failed with exit code '$?'"
                        print_errormessage
                        exit 1
                    fi
                fi                
            else
                echo "Can not find 'dnf'"
                print_errormessage
                exit 1
            fi
        else
            command -v yum
            if [ $? -eq 0 ]
            then
                yum install -y deltarpm epel-release unzip libunwind gettext libcurl-devel openssl-devel zlib libicu-devel
                if [ $? -ne 0 ]
                then                    
                    echo "'yum' failed with exit code '$?'"
                    print_errormessage
                    exit 1
                fi
            else
                echo "Can not find 'yum'"
                print_errormessage
                exit 1
            fi
        fi
    else
        # we might on OpenSUSE
        OSTYPE=$(grep ID_LIKE /etc/os-release | cut -f2 -d=)
        echo $OSTYPE
        if [ $OSTYPE == '"suse"' ]
        then
            echo "The current OS is SUSE based"
            command -v zypper
            if [ $? -eq 0 ]
            then
                zypper -n install deltarpm libunwind gettext libcurl-devel openssl-devel libicu-devel unzip
                if [ $? -ne 0 ]
                then
                    echo "'zypper' failed with exit code '$?'"
                    print_errormessage
                    exit 1
                fi
            else
                echo "Can not find 'zypper'"
                print_errormessage
                exit 1
            fi
        else
            echo "Can't detect current OS type base on /etc/os-release."
            print_errormessage
            exit 1
        fi
    fi
else
    echo "/etc/os-release doesn't exists."
    print_errormessage
    exit 1
fi

echo "-----------------------------"
echo " Finish Install Dependencies"
echo "-----------------------------"